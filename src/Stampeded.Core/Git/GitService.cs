using Stampeded.Core.Diff;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Git;

/// <summary>A checkout of the repository and the branch it has, if any.</summary>
public sealed record WorktreeCheckout(string Path, string? Branch);

/// <summary>What pulling origin's copy of a branch did to the local branch.</summary>
public enum PullOutcome
{
	/// <summary>There was no local branch of that name; it now exists at origin's commit.</summary>
	Created,
	FastForwarded,
	AlreadyUpToDate,
	/// <summary>Both sides have commits the other does not, so no fast-forward exists and
	/// nothing was changed.</summary>
	Diverged,
}

public sealed record PullResult(PullOutcome Outcome, string Sha);

/// <summary>What pushing a branch to origin did, or would have to do.</summary>
public enum PushOutcome
{
	/// <summary>Origin did not have the branch; it does now.</summary>
	Created,
	Pushed,
	/// <summary>Origin's copy was not an ancestor of the local branch - what a rebase leaves
	/// behind - so it was replaced with --force-with-lease.</summary>
	ForcePushed,
	AlreadyUpToDate,
}

public sealed record PushResult(PushOutcome Outcome, string Sha);

/// <summary>A deleted branch: the commit it pointed at, and the worktree that went with it
/// when one held the branch.</summary>
public sealed record BranchDeletion(string Sha, string? RemovedWorktree);

public enum RebaseOutcome
{
	Rebased,
	/// <summary>The merge tool left conflicts unresolved; the rebase is still in progress in
	/// <see cref="RebaseResult.WorkingDirectory"/>.</summary>
	Conflicted,
}

/// <summary>The outcome of a rebase: the branch's SHA from before it, which is the recovery
/// point if the result is unwanted, and the checkout the rebase ran in when the branch was
/// already checked out somewhere (null when a throwaway worktree was used). Recovery differs
/// between the two: a branch no checkout holds is moved with `git branch -f`, which git
/// refuses for one that is checked out - that one is recovered with `git reset --hard` in the
/// checkout, so its working tree follows the ref back.</summary>
public sealed record RebaseResult(string Before, string? Checkout, RebaseOutcome Outcome, string WorkingDirectory)
{
	public string RecoveryCommand(string branch)
		=> Checkout is null
			? $"git branch -f {branch} {Before[..9]}"
			: $"git -C {Checkout} reset --hard {Before[..9]}";
}

/// <summary>
/// Git access for one local clone, via the git CLI. Reads never touch the user's working
/// tree or index: they come from the object database (fetch, merge-base, diff, show) or,
/// for a review of uncommitted work, from a checkout's files. The operations that write
/// (branch creation, rebase) touch refs only, running any checkout they need in a throwaway
/// worktree - the one exception being a rebase of a branch that a checkout already has,
/// which has to happen in that checkout (see <see cref="RebaseBranchAsync"/>). So reviewing
/// cannot disturb what the user has checked out; only an explicit rebase can.
/// </summary>
public sealed class GitService(string repoPath)
{
	public string RepoPath => repoPath;

	Task<string> RunAsync(CancellationToken ct, params string[] args)
		=> ExternalTool.RunAsync("git", args, repoPath, ct);

	public async Task<bool> IsRepositoryAsync(CancellationToken ct = default)
	{
		try
		{
			await RunAsync(ct, "rev-parse", "--is-inside-work-tree");
			return true;
		}
		catch (ToolFailedException)
		{
			return false;
		}
	}

	public async Task<string> GetMergeBaseAsync(string a, string b, CancellationToken ct = default)
		=> (await RunAsync(ct, "merge-base", a, b)).Trim();

	public async Task<string> RevParseAsync(string reference, CancellationToken ct = default)
		=> (await RunAsync(ct, "rev-parse", "--verify", reference)).Trim();

	/// <summary>The commit a ref names, or null when there is no such ref.</summary>
	public async Task<string?> TryRevParseAsync(string reference, CancellationToken ct = default)
	{
		try
		{
			return await RevParseAsync(reference, ct);
		}
		catch (ToolFailedException)
		{
			return null;
		}
	}

	/// <summary>Updates the remote-tracking refs for every branch on origin. Sync states are
	/// computed against commits that have to be in the object database, so a branch whose PR
	/// head was never fetched reads as "differs" until this has run.</summary>
	public Task FetchAsync(CancellationToken ct = default)
		=> RunAsync(ct, "fetch", "origin");

	/// <summary>Fetches the PR head into refs/stampeded/pr/N and returns its SHA.</summary>
	public async Task<string> FetchPrHeadAsync(int number, CancellationToken ct = default)
	{
		await RunAsync(ct, "fetch", "origin", $"+refs/pull/{number}/head:refs/stampeded/pr/{number}");
		return (await RunAsync(ct, "rev-parse", $"refs/stampeded/pr/{number}")).Trim();
	}

	public Task FetchBranchAsync(string branch, CancellationToken ct = default)
		=> RunAsync(ct, "fetch", "origin", branch);

	public async Task<IReadOnlyList<FileDiff>> DiffAsync(string baseRev, string headRev, CancellationToken ct = default)
		=> GitDiffParser.Parse(await RunAsync(ct, "diff", "-U3", "--find-renames", baseRev, headRev));

	/// <summary>The checkouts of this repository - the main one and any linked worktrees -
	/// with the branch each has checked out (null when detached).</summary>
	public async Task<IReadOnlyList<WorktreeCheckout>> ListWorktreesAsync(CancellationToken ct = default)
	{
		var checkouts = new List<WorktreeCheckout>();
		string? path = null;
		string? branch = null;
		foreach (var line in (await RunAsync(ct, "worktree", "list", "--porcelain")).ReplaceLineEndings("\n").Split('\n'))
		{
			if (line.StartsWith("worktree ", StringComparison.Ordinal))
			{
				if (path is not null)
					checkouts.Add(new WorktreeCheckout(path, branch));
				path = line["worktree ".Length..].Trim();
				branch = null;
			}
			else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
			{
				branch = line["branch refs/heads/".Length..].Trim();
			}
		}
		if (path is not null)
			checkouts.Add(new WorktreeCheckout(path, branch));
		return checkouts;
	}

	/// <summary>Whether a checkout has changes that are not committed - staged, unstaged
	/// or untracked.</summary>
	public async Task<bool> IsDirtyAsync(string worktreePath, CancellationToken ct = default)
		=> (await ExternalTool.RunAsync("git", ["status", "--porcelain"], worktreePath, ct)).Trim().Length > 0;

	/// <summary>
	/// A checkout's current contents against a commit: everything `git diff &lt;base&gt;` reports
	/// (staged and unstaged alike, since the comparison is with the working tree), plus the
	/// untracked files, which that diff omits and which are read individually so the index
	/// is never touched.
	/// </summary>
	public async Task<IReadOnlyList<FileDiff>> DiffWorkingTreeAsync(
		string worktreePath, string baseRev, CancellationToken ct = default)
	{
		var files = GitDiffParser.Parse(await ExternalTool.RunAsync(
			"git", ["diff", "-U3", "--find-renames", baseRev], worktreePath, ct)).ToList();
		string untracked = await ExternalTool.RunAsync(
			"git", ["ls-files", "--others", "--exclude-standard"], worktreePath, ct);
		foreach (var relPath in untracked.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			// --no-index reports "differences found" as exit 1, which is the normal case here.
			string diff = await ExternalTool.RunAsync(
				"git", ["diff", "-U3", "--no-index", "--", "/dev/null", relPath],
				worktreePath, ct, okExitCodes: [1]);
			files.AddRange(GitDiffParser.Parse(diff));
		}
		return [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
	}

	public Task<string> ShowFileAsync(string rev, string path, CancellationToken ct = default)
		=> RunAsync(ct, "show", $"{rev}:{path}");

	public async Task<IReadOnlyList<BlameLine>> BlameAsync(string rev, string path, CancellationToken ct = default)
		=> GitBlameParser.Parse(await RunAsync(ct, "blame", "--porcelain", rev, "--", path));

	const string LogFormat = "--format=%H%x09%h%x09%an%x09%ad%x09%s%n%b%x00";

	public async Task<IReadOnlyList<CommitInfo>> LogAsync(
		string? range, string? path, bool follow, int limit, CancellationToken ct = default)
	{
		var args = new List<string> { "log", LogFormat, "--date=short", $"-n{limit}" };
		if (range is not null)
			args.Add(range);
		if (follow)
			args.Add("--follow");
		if (path is not null)
		{
			args.Add("--");
			args.Add(path);
		}
		return GitLogParser.Parse(await RunAsync(ct, args.ToArray()));
	}

	/// <summary>Commits whose diff adds or removes the given text (`git log -S`).</summary>
	public async Task<IReadOnlyList<CommitInfo>> LogPickaxeAsync(
		string text, string? path, int limit, CancellationToken ct = default)
	{
		var args = new List<string> { "log", LogFormat, "--date=short", $"-n{limit}", $"-S{text}" };
		if (path is not null)
		{
			args.Add("--");
			args.Add(path);
		}
		return GitLogParser.Parse(await RunAsync(ct, args.ToArray()));
	}

	public async Task<IReadOnlyList<(char Status, string Path)>> DiffNameStatusAsync(
		string a, string b, CancellationToken ct = default)
		=> GitLogParser.ParseNameStatus(await RunAsync(ct, "diff", "--name-status", "--find-renames", a, b));

	/// <summary>
	/// The local branches whose tip is reachable from <paramref name="intoRef"/> - what
	/// `git branch --merged` reports. It is an ancestry test, so it answers "is this in there
	/// as it stands", and it is the cheap half of the question: one call for every branch.
	/// A rebase-merged branch is not among them, because its commits were replayed and none
	/// of the originals survives in the target - see <see cref="IsMergedByPatchAsync"/>.
	/// </summary>
	public async Task<IReadOnlySet<string>> ListMergedBranchesAsync(string intoRef, CancellationToken ct = default)
	{
		string output = await RunAsync(ct, "branch", "--merged", intoRef, "--format=%(refname:short)");
		return output.ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Select(line => line.Trim())
			.Where(line => line.Length > 0)
			.ToHashSet(StringComparer.Ordinal);
	}

	/// <summary>
	/// Whether every commit on <paramref name="branch"/> already exists in
	/// <paramref name="intoRef"/> as an equivalent patch, which is how a rebase-merged branch
	/// looks: same changes, different commits, so ancestry says no and this says yes.
	/// `git cherry` marks a commit "-" when the upstream has one with the same patch id and
	/// "+" when it does not, so the branch is in when nothing is marked "+".
	///
	/// A branch that has no commits of its own answers true, which is right - there is
	/// nothing of it left to merge. A squash-merged branch of more than one commit answers
	/// false, because its commits were combined into one whose patch matches none of them.
	/// </summary>
	public async Task<bool> IsMergedByPatchAsync(string branch, string intoRef, CancellationToken ct = default)
	{
		string output = await RunAsync(ct, "cherry", intoRef, branch);
		return !output.ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries)
			.Any(line => line.StartsWith('+'));
	}

	/// <summary>
	/// Deletes a local branch, along with the worktree holding it when there is one - git
	/// refuses to delete a branch some checkout has, so the two go together or not at all.
	/// Returns the commit the branch pointed at, which is what it takes to offer the branch
	/// back; nothing refers to that commit afterwards, so it lives on only in the reflog
	/// until git expires it.
	///
	/// The worktree is removed without --force on purpose. "The branch is merged" says the
	/// commits are safe somewhere else; it says nothing about uncommitted edits sitting in
	/// that directory, and git's refusal to discard them is the only thing standing between
	/// this button and losing them.
	///
	/// This deletes with -D, which is not the shortcut it looks like. `git branch -d` tests
	/// the branch against its upstream, or against HEAD when it has none - neither of which
	/// is the default branch. That answers a different question than the caller asked, and
	/// gets it wrong in both directions: it refuses a branch that is an ancestor of the
	/// default branch while HEAD happens to lag behind it, and it has no way to recognise a
	/// rebase merge at all. The caller establishes the fact that matters against the ref that
	/// matters; there is no second opinion here worth having.
	/// </summary>
	public async Task<BranchDeletion> DeleteBranchAsync(string branch, CancellationToken ct = default)
	{
		string sha = await RevParseAsync($"refs/heads/{branch}", ct);
		string? removedWorktree = null;
		if (await FindCheckoutAsync(branch, ct) is { } checkout)
		{
			await RemoveWorktreeAsync(checkout.Path, ct);
			removedWorktree = checkout.Path;
		}
		await RunAsync(ct, "branch", "-D", branch);
		return new BranchDeletion(sha, removedWorktree);
	}

	/// <summary>
	/// Removes a worktree, falling back to deleting the directory when git will not do it.
	/// `git worktree remove` rejects any worktree containing submodules outright - the check
	/// runs before --force is even consulted, so there is no flag that gets past it, and a
	/// repository with a submodule could otherwise never have a worktree removed here.
	///
	/// The fallback establishes for itself what git would have enforced: the worktree has to
	/// be clean, submodules included, or nothing is deleted.
	/// </summary>
	async Task RemoveWorktreeAsync(string path, CancellationToken ct)
	{
		try
		{
			await RunAsync(ct, "worktree", "remove", path);
			return;
		}
		catch (ToolFailedException ex) when (ex.StdErr.Contains("submodules", StringComparison.Ordinal))
		{
		}
		string status = await ExternalTool.RunAsync(
			"git", ["status", "--porcelain", "--ignore-submodules=none"], path, ct);
		if (status.Trim().Length > 0)
		{
			throw new RefusedException(
				$"'{path}' contains modified or untracked files. It holds submodules, so git will not "
				+ "remove it and it would have to be deleted outright - which is not something to do "
				+ "to uncommitted work. Nothing was deleted.");
		}
		Directory.Delete(path, recursive: true);
		// The worktree's administrative entry outlives the directory, and the branch stays
		// checked out as far as git is concerned until it is gone.
		await RunAsync(ct, "worktree", "prune");
	}

	/// <summary>Local branches, most recently committed first.</summary>
	public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(CancellationToken ct = default)
		=> GitLogParser.ParseBranches(await RunAsync(ct,
			"for-each-ref", "refs/heads", "--sort=-committerdate",
			"--format=%(refname:short)%09%(objectname)%09%(committerdate:short)%09%(subject)"));

	/// <summary>How a local branch stands against another commit, or null when that commit
	/// is not in the local object database - which is the normal case for a pull request
	/// head that was never fetched.</summary>
	public async Task<BranchSync?> GetSyncStateAsync(string local, string remote, CancellationToken ct = default)
	{
		try
		{
			string output = await RunAsync(ct, "rev-list", "--left-right", "--count", $"{local}...{remote}");
			var parts = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			return parts.Length == 2 && int.TryParse(parts[0], out int ahead) && int.TryParse(parts[1], out int behind)
				? BranchSync.From(ahead, behind)
				: null;
		}
		catch (ToolFailedException)
		{
			return null;
		}
	}

	/// <summary>The stashes, in the same tab-separated shape as the branch listing so both
	/// go through one parser. A stash's own commit holds the stashed working tree, and its
	/// first parent is the commit it was taken on - so <c>sha^..sha</c> is exactly what
	/// `git stash show` reports, and a stash reviews as an ordinary local range.</summary>
	public async Task<IReadOnlyList<BranchInfo>> ListStashesAsync(CancellationToken ct = default)
		=> GitLogParser.ParseBranches(await RunAsync(ct,
			"stash", "list", "--format=%gd%x09%H%x09%cs%x09%gs"));

	/// <summary>Points a new branch at an existing commit. Used to give a stash a durable
	/// name: the stash itself is left in place, and nothing is checked out or applied
	/// (unlike `git stash branch`, which would rewrite the user's working tree).</summary>
	public Task CreateBranchAsync(string name, string startPoint, CancellationToken ct = default)
		=> RunAsync(ct, "branch", name, startPoint);

	/// <summary>
	/// Rebases a local branch onto another ref in a throwaway worktree, leaving the user's
	/// checkout alone.
	///
	/// A branch that a checkout already has is rebased in that checkout instead: git allows
	/// a branch in only one checkout at a time, so a throwaway worktree cannot have it. That
	/// moves the checkout's working tree and index along with the ref, which is the point -
	/// updating the ref behind its back would leave it describing a commit the branch no
	/// longer points at. Git's own refusals there (uncommitted changes, a rebase already in
	/// progress) are passed through unchanged.
	///
	/// Conflicts open the configured merge tool rather than ending the rebase, and each
	/// resolved step is continued automatically. What the tool leaves unresolved stays
	/// unresolved: the rebase is left in progress in <see cref="RebaseResult.WorkingDirectory"/>
	/// for the user to finish or abort, because discarding it would throw away the
	/// resolutions they just made.
	/// </summary>
	public async Task<RebaseResult> RebaseBranchAsync(string branch, string onto, CancellationToken ct = default)
	{
		string before = await RevParseAsync(branch, ct);
		var checkout = await FindCheckoutAsync(branch, ct);
		string dir = checkout?.Path
			?? Path.Combine(Path.GetTempPath(), "stampeded-rebase-" + Guid.NewGuid().ToString("N")[..8]);
		if (checkout is null)
			await RunAsync(ct, "worktree", "add", "--quiet", dir, branch);
		bool leaveInPlace = false;
		try
		{
			try
			{
				await ExternalTool.RunAsync("git", ["rebase", onto], dir, ct);
			}
			catch (ToolFailedException)
			{
				// Without conflicts it never started (a dirty checkout, a bad ref): nothing
				// is in progress to abort, the branch is untouched, and the failure is the
				// whole answer.
				if (!await HasUnmergedFilesAsync(dir, ct))
					throw;
				if (!await ResolveConflictsAsync(dir, ct))
				{
					leaveInPlace = true;
					return new RebaseResult(before, checkout?.Path, RebaseOutcome.Conflicted, dir);
				}
			}
			return new RebaseResult(before, checkout?.Path, RebaseOutcome.Rebased, dir);
		}
		finally
		{
			if (checkout is null && !leaveInPlace)
			{
				try
				{
					await RunAsync(CancellationToken.None, "worktree", "remove", "--force", dir);
				}
				catch (ToolFailedException)
				{
					await RunAsync(CancellationToken.None, "worktree", "prune");
				}
			}
		}
	}

	/// <summary>Paths git reports as unmerged - the conflicts a rebase stopped on.</summary>
	async Task<bool> HasUnmergedFilesAsync(string dir, CancellationToken ct)
		=> (await ExternalTool.RunAsync("git", ["diff", "--name-only", "--diff-filter=U"], dir, ct)).Trim().Length > 0;

	/// <summary>
	/// Runs the user's merge tool over each conflicted step and continues the rebase, until
	/// it finishes or the tool leaves something unresolved (the user closed it without
	/// deciding, or none is configured). True only when the rebase ran to completion.
	/// </summary>
	async Task<bool> ResolveConflictsAsync(string dir, CancellationToken ct)
	{
		// A rebase stops once per conflicting commit, so this is a loop, not one pass. The
		// bound is a backstop against a tool that exits without ever resolving anything.
		for (int step = 0; step < 50; step++)
		{
			try
			{
				// -y: git otherwise prompts on a terminal this process does not have.
				await ExternalTool.RunAsync("git", ["mergetool", "-y"], dir, ct);
			}
			catch (ToolFailedException)
			{
				return false;
			}
			if (await HasUnmergedFilesAsync(dir, ct))
				return false;
			bool continued;
			try
			{
				// GIT_EDITOR=true accepts the existing commit message: the rebase is being
				// driven from a UI with nowhere to show an editor.
				await ExternalTool.RunAsync("git", ["rebase", "--continue"], dir, ct,
					env: new Dictionary<string, string> { ["GIT_EDITOR"] = "true" });
				continued = true;
			}
			catch (ToolFailedException)
			{
				continued = false;
			}
			// Continuing stops again on the next conflicting commit, which is another round;
			// a failure with nothing unmerged is something this cannot drive.
			if (continued)
				return true;
			if (!await HasUnmergedFilesAsync(dir, ct))
				return false;
		}
		return false;
	}

	/// <summary>
	/// Brings origin's copy of a branch into the local repository: creates the local branch
	/// when it does not exist yet, fast-forwards it when it has fallen behind, and refuses
	/// when the two have diverged - that case needs a rebase, which is a different decision
	/// and is offered separately. Never merges, so the branch either moves along its own
	/// history or is left alone.
	/// </summary>
	public async Task<PullResult> PullBranchAsync(string branch, CancellationToken ct = default)
	{
		await FetchBranchAsync(branch, ct);
		string target = await RevParseAsync("FETCH_HEAD", ct);
		if (await TryRevParseAsync($"refs/heads/{branch}", ct) is not { } local)
		{
			await RunAsync(ct, "branch", branch, target);
			return new PullResult(PullOutcome.Created, target);
		}
		if (string.Equals(local, target, StringComparison.OrdinalIgnoreCase))
			return new PullResult(PullOutcome.AlreadyUpToDate, target);
		if (await GetMergeBaseAsync(local, target, ct) != local)
			return new PullResult(PullOutcome.Diverged, target);
		// Same constraint as a rebase: git allows a branch in one checkout at a time, and a
		// checkout that has it has to move with it rather than be left behind.
		if (await FindCheckoutAsync(branch, ct) is { } checkout)
			await ExternalTool.RunAsync("git", ["merge", "--ff-only", target], checkout.Path, ct);
		else
			await RunAsync(ct, "branch", "--force", branch, target);
		return new PullResult(PullOutcome.FastForwarded, target);
	}

	/// <summary>
	/// Pushes a local branch to origin, force-pushing when origin's copy is not an ancestor
	/// of it - the state a rebase leaves behind, where a plain push can only be rejected.
	///
	/// Forcing uses --force-with-lease, so it still refuses when origin has moved since the
	/// last fetch: it overwrites the commits it knows about, not ones it has never seen.
	/// Nothing here fetches first, deliberately - that would refresh the very ref the lease
	/// is compared against and turn the guarantee back into a plain --force.
	/// </summary>
	public async Task<PushResult> PushBranchAsync(string branch, CancellationToken ct = default)
	{
		string local = await RevParseAsync($"refs/heads/{branch}", ct);
		string? remote = await TryRevParseAsync($"refs/remotes/origin/{branch}", ct);
		if (remote is not null && string.Equals(remote, local, StringComparison.OrdinalIgnoreCase))
			return new PushResult(PushOutcome.AlreadyUpToDate, local);
		bool fastForward = remote is null || await GetMergeBaseAsync(remote, local, ct) == remote;
		if (fastForward)
			await RunAsync(ct, "push", "origin", branch);
		else
			await RunAsync(ct, "push", "--force-with-lease", "origin", branch);
		return new PushResult(
			remote is null ? PushOutcome.Created : fastForward ? PushOutcome.Pushed : PushOutcome.ForcePushed,
			local);
	}

	/// <summary>The checkout that has this branch, if any. A branch can be in only one.</summary>
	async Task<WorktreeCheckout?> FindCheckoutAsync(string branch, CancellationToken ct)
		=> (await ListWorktreesAsync(ct)).FirstOrDefault(w => w.Branch == branch);

	/// <summary>The review base for local branches: origin's default branch when known,
	/// else origin/master.</summary>
	public async Task<string> GetDefaultBaseAsync(CancellationToken ct = default)
	{
		try
		{
			return (await RunAsync(ct, "rev-parse", "--abbrev-ref", "origin/HEAD")).Trim();
		}
		catch (Infra.ToolFailedException)
		{
			return "origin/master";
		}
	}
}
