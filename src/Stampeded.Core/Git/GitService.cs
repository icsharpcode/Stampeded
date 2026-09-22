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

/// <summary>What git is in the middle of in one checkout, and cannot be talked to normally
/// until it is finished or abandoned.</summary>
public enum GitOperation
{
	Rebase,
	Merge,
	CherryPick,
	Revert,
	Bisect,
}

/// <summary>
/// An operation git has half-finished somewhere. <paramref name="WorkingDirectory"/> is the
/// checkout it is in, which is not always the one the reader is looking at: a rebase of a
/// branch no checkout holds runs in a worktree made for it, and that worktree is deliberately
/// left behind when the rebase stops on a conflict, so the work is still there to finish.
/// </summary>
/// <param name="IsScratch">The checkout exists only for this operation, so abandoning the
/// operation should take the checkout with it rather than leave it to be found later.</param>
/// <param name="Unmerged">How many files are still conflicted. It decides which way out is
/// open: what is conflicted has to be resolved before it can be continued.</param>
public sealed record InProgressOperation(
	GitOperation Kind, string WorkingDirectory, string? Branch, bool IsScratch, int Unmerged)
{
	/// <summary>Resolving means running the merge tool over what is still conflicted.</summary>
	public bool CanResolve => Unmerged > 0 && Kind is not GitOperation.Bisect;

	/// <summary>Only once nothing is conflicted: git refuses otherwise, and offering a button
	/// that git will refuse is how the merge tool came to look optional.</summary>
	public bool CanContinue => Unmerged == 0 && Kind is not GitOperation.Bisect;

	/// <summary>A merge has one commit to make, so there is nothing to skip past.</summary>
	public bool CanSkip => Kind is GitOperation.Rebase or GitOperation.CherryPick or GitOperation.Revert;

	/// <summary>What abandoning it would run, for a status line that says what it did.</summary>
	public string AbortCommand => Kind switch {
		GitOperation.Rebase => "git rebase --abort",
		GitOperation.Merge => "git merge --abort",
		GitOperation.CherryPick => "git cherry-pick --abort",
		GitOperation.Revert => "git revert --abort",
		_ => "git bisect reset",
	};

	public string Name => Kind switch {
		GitOperation.Rebase => "Rebase",
		GitOperation.Merge => "Merge",
		GitOperation.CherryPick => "Cherry-pick",
		GitOperation.Revert => "Revert",
		_ => "Bisect",
	};

	/// <summary>
	/// One line, short enough to sit beside the buttons that act on it. The path is the least
	/// useful part of the sentence and the longest, so it is not in here: <see cref="Describe"/>
	/// has it for the tooltip, and a checkout that exists only for the operation has a name
	/// worth less than saying so.
	/// </summary>
	public string Headline => $"{Name}{(Branch is { Length: > 0 } b ? $" of {b}" : "")}"
		+ (Unmerged > 0
			// What is conflicted is a decision waiting to be made; what is not is simply
			// stopped part-way, which is a different thing to tell somebody.
			? $" - {Unmerged} file{(Unmerged == 1 ? "" : "s")} conflicted"
			: " - paused, nothing conflicted");

	/// <summary>Where it is, in as few characters as still identify the place.</summary>
	public string Where => IsScratch
		? "a worktree made for it"
		: ShortPath(WorkingDirectory);

	public string Describe => $"{Headline}, in {(IsScratch ? "a worktree made for it: " : "")}{WorkingDirectory}";

	/// <summary>Home collapsed to ~, and a long path down to its last two segments: the reader
	/// is being told which checkout, not being given something to copy.</summary>
	static string ShortPath(string path)
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string shown = home.Length > 0 && path.StartsWith(home, StringComparison.Ordinal)
			? "~" + path[home.Length..] : path;
		if (shown.Length <= 48)
			return shown;
		var parts = shown.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
		return parts.Length <= 2 ? shown : ".../" + string.Join('/', parts[^2..]);
	}
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

	Task<string>? remote;

	/// <summary>
	/// The remote this tool fetches from and pushes to. Asked once per service: a clone does
	/// not rename its remotes while it is being reviewed. Throws <see cref="ToolFailedException"/>
	/// with a sentence a status line can show when there is no remote, or several and nothing
	/// says which one is meant - guessing there would fetch from and push to the wrong place.
	/// </summary>
	public Task<string> GetRemoteAsync(CancellationToken ct = default)
	{
		// A failed answer is not kept: the reader may add the remote or set the config the
		// message asks for, and the next attempt should see it.
		if (remote is { IsFaulted: true } or { IsCanceled: true })
			remote = null;
		return remote ??= ResolveRemoteAsync(ct);
	}

	async Task<string> ResolveRemoteAsync(CancellationToken ct)
	{
		var remotes = (await RunAsync(ct, "remote")).ReplaceLineEndings("\n")
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		string? defaultRemote = await ConfigAsync("checkout.defaultRemote", ct);
		string? headRemote = null;
		// Exit 1 is a detached HEAD, which tracks nothing.
		string head = (await ExternalTool.RunAsync("git", ["symbolic-ref", "--quiet", "--short", "HEAD"], repoPath, ct,
			okExitCodes: [1])).Trim();
		if (head.Length > 0)
			headRemote = await ConfigAsync($"branch.{head}.remote", ct);
		if (ChooseRemote(remotes, defaultRemote, headRemote) is { } chosen)
		{
			if (chosen != "origin")
				CliLog.Write("git", $"using remote '{chosen}'");
			return chosen;
		}
		string reason = remotes.Length == 0
			? "This repository has no git remote to fetch from. Add one with: git remote add origin <url>"
			: $"This repository has remotes {string.Join(", ", remotes)} and none is named origin. "
				+ "Say which one to use with: git config checkout.defaultRemote <name>";
		CliLog.Write("git", reason);
		throw new ToolFailedException("git", -1, reason);
	}

	/// <summary>A git config value, or null when it is not set - which git answers with exit 1.</summary>
	async Task<string?> ConfigAsync(string key, CancellationToken ct)
		=> (await ExternalTool.RunAsync("git", ["config", "--get", key], repoPath, ct, okExitCodes: [1])).Trim()
			is { Length: > 0 } value ? value : null;

	/// <summary>
	/// Which of a clone's remotes is the one meant. git's own <c>checkout.defaultRemote</c> is
	/// asked first, because it is the reader saying so; then origin, which is what a clone
	/// calls the place it came from; then the only remote there is; then the remote the
	/// checked-out branch tracks. Null when none of these names a remote that exists.
	/// </summary>
	public static string? ChooseRemote(IReadOnlyList<string> remotes, string? defaultRemote, string? headRemote)
	{
		if (defaultRemote is not null && remotes.Contains(defaultRemote))
			return defaultRemote;
		if (remotes.Contains("origin"))
			return "origin";
		if (remotes.Count == 1)
			return remotes[0];
		if (headRemote is not null && remotes.Contains(headRemote))
			return headRemote;
		return null;
	}

	/// <summary>The remote-tracking ref for a branch: origin/main, or whatever the remote is called.</summary>
	public async Task<string> RemoteBranchAsync(string branch, CancellationToken ct = default)
		=> $"{await GetRemoteAsync(ct)}/{branch}";

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

	/// <summary>Whether the object <paramref name="reference"/> names is really in the object
	/// database. `rev-parse --verify` answers a full SHA with itself whether or not it is
	/// there; only asking for the commit it names reads the database.</summary>
	public async Task<bool> HasCommitAsync(string reference, CancellationToken ct = default)
		=> await TryRevParseAsync($"{reference}^{{commit}}", ct) is not null;

	/// <summary>Whether every commit of <paramref name="ancestor"/> is already contained in
	/// <paramref name="descendant"/> - the difference between a push that added commits and
	/// one that rewrote them. Counted with rev-list rather than asked of
	/// `merge-base --is-ancestor`, whose answer is its exit code, which this process's tool
	/// runner reports as a failed command with a log line to match.</summary>
	public async Task<bool> IsAncestorAsync(string ancestor, string descendant, CancellationToken ct = default)
		=> (await RunAsync(ct, "rev-list", "--count", $"{descendant}..{ancestor}")).Trim() == "0";

	/// <summary>
	/// The tree <paramref name="head"/>'s work makes when replayed onto <paramref name="onto"/>:
	/// the content a rebase would produce, computed entirely in the object database, so no
	/// worktree, index or ref is touched. <paramref name="mergeBase"/> names the base that
	/// work was written against, for the case where the branch was rebased onto somewhere
	/// else entirely and git's own merge base would be further back than the work.
	///
	/// Null when the replay conflicts: merge-tree still prints a tree then, but one with
	/// conflict markers in it, and there is no honest way to show that as the author's code.
	/// Any other failure is the caller's to report.
	/// </summary>
	public async Task<string?> ReplayTreeAsync(string onto, string head, string? mergeBase, CancellationToken ct = default)
	{
		string[] args = mergeBase is null
			? ["merge-tree", "--write-tree", onto, head]
			: ["merge-tree", "--write-tree", $"--merge-base={mergeBase}", onto, head];
		try
		{
			return (await RunAsync(ct, args)).Trim();
		}
		catch (ToolFailedException ex) when (ex.ExitCode == 1)
		{
			return null;
		}
	}

	/// <summary>
	/// Keeps the two commits a re-review needs reachable: the head being read now, and the
	/// head of the pass before it. Neither is safe on its own - the PR head ref is
	/// force-updated by every fetch, refs/stampeded/pr/N has no reflog, and a rewritten
	/// branch's old tip is referenced by nothing at all - so without a ref of the tool's own,
	/// the commit the reader compared against last time is prunable.
	/// </summary>
	public async Task PinReviewHeadsAsync(string key, string head, string? previousHead, CancellationToken ct = default)
	{
		await RunAsync(ct, "update-ref", $"refs/stampeded/review/{key}/head", head);
		if (previousHead is not null)
			await RunAsync(ct, "update-ref", $"refs/stampeded/review/{key}/prev", previousHead);
	}

	/// <summary>Updates the remote-tracking refs for every branch on the remote. Sync states are
	/// computed against commits that have to be in the object database, so a branch whose PR
	/// head was never fetched reads as "differs" until this has run.</summary>
	public async Task FetchAsync(CancellationToken ct = default)
		=> await RunAsync(ct, "fetch", await GetRemoteAsync(ct));

	/// <summary>Fetches the PR head into refs/stampeded/pr/N and returns its SHA. The refspec
	/// comes from the host: GitHub advertises every pull request's head as a ref of its own,
	/// Azure DevOps does not and the source branch is fetched instead.</summary>
	public async Task<string> FetchPrHeadAsync(string refspec, int number, CancellationToken ct = default)
	{
		await RunAsync(ct, "fetch", await GetRemoteAsync(ct), refspec);
		return (await RunAsync(ct, "rev-parse", $"refs/stampeded/pr/{number}")).Trim();
	}

	public async Task FetchBranchAsync(string branch, CancellationToken ct = default)
		=> await RunAsync(ct, "fetch", await GetRemoteAsync(ct), branch);

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

	/// <summary>Whether a checkout has changes that are not committed - staged or unstaged.
	/// Untracked files do not count: they are not part of the change under review, and a
	/// checkout that has nothing but build output in it is not a review step.</summary>
	public async Task<bool> IsDirtyAsync(string worktreePath, CancellationToken ct = default)
		=> (await ExternalTool.RunAsync(
			"git", ["status", "--porcelain", "--untracked-files=no"], worktreePath, ct)).Trim().Length > 0;

	/// <summary>
	/// Every file a revision has, repository-relative. Read from the object database rather
	/// than from a checkout, so it is the revision's list and not whatever a working tree
	/// happens to hold - and so it costs nothing when no checkout exists yet.
	/// </summary>
	public async Task<IReadOnlyList<string>> ListFilesAsync(string revision, CancellationToken ct = default)
	{
		string output = await RunAsync(ct, "ls-tree", "-r", "--name-only", "-z", revision);
		return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
	}

	/// <summary>
	/// A checkout's current contents against a commit: everything `git diff &lt;base&gt;` reports,
	/// staged and unstaged alike, since the comparison is with the working tree. Untracked
	/// files are not in it - git does not track them and neither does a review.
	/// </summary>
	public async Task<IReadOnlyList<FileDiff>> DiffWorkingTreeAsync(
		string worktreePath, string baseRev, CancellationToken ct = default)
	{
		var files = GitDiffParser.Parse(await ExternalTool.RunAsync(
			"git", ["diff", "-U3", "--find-renames", baseRev], worktreePath, ct)).ToList();
		return [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
	}

	public Task<string> ShowFileAsync(string rev, string path, CancellationToken ct = default)
		=> RunAsync(ct, "show", $"{rev}:{path}");

	/// <summary>
	/// How many bytes a revision's copy of a file has, or null when that revision does not
	/// have it - which is the ordinary answer for the base side of an addition and the head
	/// side of a deletion, not a failure.
	///
	/// Asked of the object database rather than read, because the file this is wanted for is
	/// the one that cannot be read as text: how much a binary file grew is the whole of what
	/// a review can say about it.
	/// </summary>
	public async Task<long?> BlobSizeAsync(string rev, string path, CancellationToken ct = default)
	{
		try
		{
			return long.TryParse((await RunAsync(ct, "cat-file", "-s", $"{rev}:{path}")).Trim(), out long size)
				? size
				: null;
		}
		catch (ToolFailedException)
		{
			return null;
		}
	}

	/// <summary>A whole commit as a patch - its message and its diff, the way git prints it.</summary>
	public Task<string> ShowCommitAsync(string rev, CancellationToken ct = default)
		=> RunAsync(ct, "show", rev);

	/// <summary>The diff between two commits as a patch, for reading as text rather than as a
	/// per-file review.</summary>
	public Task<string> DiffPatchAsync(string baseRev, string headRev, CancellationToken ct = default)
		=> RunAsync(ct, "diff", baseRev, headRev);

	/// <summary>Lines added and removed by each commit of a range, in one pass over it rather
	/// than a diff per commit.</summary>
	public async Task<IReadOnlyDictionary<string, (int Added, int Removed)>> GetCommitStatsAsync(
		string baseRev, string headRev, CancellationToken ct = default)
		=> GitLogParser.ParseShortStat(
			await RunAsync(ct, "log", "--format=%H", "--shortstat", $"{baseRev}..{headRev}"));

	/// <summary>How many commits touched each file in the recent past - the repository's hot
	/// spots. <paramref name="since"/> is a git date expression such as "1.year".</summary>
	public async Task<IReadOnlyDictionary<string, int>> GetChurnAsync(
		string since, CancellationToken ct = default)
		=> GitLogParser.CountPathTouches(
			await RunAsync(ct, "log", $"--since={since}", "--name-only", "--format="));

	public async Task<IReadOnlyList<BlameLine>> BlameAsync(string rev, string path, CancellationToken ct = default)
		=> GitBlameParser.Parse(await RunAsync(ct, "blame", "--porcelain", rev, "--", path));

	// The subject stays last on the header line: it is the one field that can hold a tab, so
	// anything added after it would be cut out of a subject that contains one.
	const string LogFormat = "--format=%H%x09%h%x09%an%x09%ad%x09%P%x09%s%n%b%x00";

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
	/// How many commits each local branch has that a base does not, keyed by branch name -
	/// which is how many commits reviewing it would mean reading.
	///
	/// One call for every branch, because git can answer it for all of them at once
	/// (%(ahead-behind:...), git 2.41 and later). Asked branch by branch it was one process
	/// each, and a repository has as many branches as it has.
	/// </summary>
	public async Task<IReadOnlyDictionary<string, int>> GetAheadCountsAsync(
		string baseRef, CancellationToken ct = default)
	{
		string output = await RunAsync(ct,
			"for-each-ref", "refs/heads", $"--format=%(refname:short)%09%(ahead-behind:{baseRef})");
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (string line in output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			// "<branch>\t<ahead> <behind>", and an empty field for a branch git could not
			// compare - an older git, or a base that is not there.
			var parts = line.Split('\t');
			if (parts.Length != 2)
				continue;
			var counted = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (counted.Length == 2 && int.TryParse(counted[0], out int ahead))
				counts[parts[0]] = ahead;
		}
		return counts;
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
	/// <param name="progress">Where the rebase has got to, in words. The merge tool is a child
	/// process with no terminal of its own and a window that need not come to the front, so a
	/// rebase that stops on a conflict otherwise looks like one that stopped responding.</param>
	public async Task<RebaseResult> RebaseBranchAsync(string branch, string onto,
		IProgress<string>? progress = null, CancellationToken ct = default)
	{
		progress?.Report($"Rebasing {branch} onto {onto}...");
		// Before anything else, and by the branch rather than by the checkout: a checkout in
		// the middle of a rebase is detached, so it does not look like it holds the branch at
		// all - which is how a retry used to reach git and come back with a fatal about a
		// rebase-merge directory, having never run the merge tool.
		if ((await ListInProgressAsync(ct)).FirstOrDefault(o => o.Branch == branch) is { } already)
		{
			throw new RefusedException(
				$"A {already.Name.ToLowerInvariant()} of {branch} is already in progress in "
				+ $"{already.Where}. Finish it or abandon it first.");
		}
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
				if (!await ResolveConflictsAsync(dir, progress, ct))
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
					// Pruning deregisters the worktree; it does not remove the directory, and a
					// checkout left in a temporary directory forever is nobody's to find later.
					try
					{
						Directory.Delete(dir, recursive: true);
					}
					catch (Exception e) when (e is IOException or UnauthorizedAccessException)
					{
						CliLog.Write("worktree", $"could not remove {dir}: {e.Message}");
					}
				}
			}
		}
	}

	/// <summary>Paths git reports as unmerged - the conflicts a rebase stopped on.</summary>
	async Task<bool> HasUnmergedFilesAsync(string dir, CancellationToken ct)
		=> await CountUnmergedAsync(dir, ct) > 0;

	/// <summary>The paths git reports as unmerged, one per line.</summary>
	static async Task<IReadOnlyList<string>> UnmergedPathsAsync(string dir, CancellationToken ct)
	{
		try
		{
			return [.. (await ExternalTool.RunAsync("git", ["diff", "--name-only", "--diff-filter=U"], dir, ct))
				.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
		}
		catch (ToolFailedException)
		{
			return [];
		}
	}

	/// <summary>
	/// Which of these files still carry the markers git writes into a conflict. This is the
	/// check that a resolution actually happened: a merge tool reports success by exiting, and
	/// an editor somebody closed exits just as cleanly as one they finished.
	/// </summary>
	static async Task<IReadOnlyList<string>> WithConflictMarkersAsync(
		string dir, IReadOnlyList<string> paths, CancellationToken ct)
	{
		var left = new List<string>();
		foreach (string path in paths)
		{
			try
			{
				string full = Path.Combine(dir, path);
				if (!File.Exists(full))
					continue;
				string text = await File.ReadAllTextAsync(full, ct);
				// Both ends, at the start of a line: one alone is ordinary text often enough
				// (a diff quoted in a comment), and both is what git actually leaves.
				if (HasMarker(text, "<<<<<<<") && HasMarker(text, ">>>>>>>"))
					left.Add(path);
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				// A file that cannot be read is not one this can vouch for either way.
			}
		}
		return left;

		static bool HasMarker(string text, string marker)
			=> text.ReplaceLineEndings("\n").Split('\n').Any(l => l.StartsWith(marker, StringComparison.Ordinal));
	}

	async Task<int> CountUnmergedAsync(string dir, CancellationToken ct)
	{
		try
		{
			return (await ExternalTool.RunAsync("git", ["diff", "--name-only", "--diff-filter=U"], dir, ct))
				.ReplaceLineEndings("\n").Split('\n').Count(l => l.Trim().Length > 0);
		}
		catch (ToolFailedException)
		{
			return 0;
		}
	}

	/// <summary>
	/// Runs the configured merge tool over what is still conflicted. The tool is a window of its
	/// own that need not come to the front, which is why this is a button rather than something
	/// a rebase does silently on the reader's behalf.
	/// </summary>
	public Task RunMergeToolAsync(InProgressOperation operation, CancellationToken ct = default)
		// -y: git otherwise prompts on a terminal this process does not have.
		=> ExternalTool.RunAsync("git", ["mergetool", "-y"], operation.WorkingDirectory, ct);

	/// <summary>Carries the operation on past the step it stopped at.</summary>
	public Task ContinueAsync(InProgressOperation operation, CancellationToken ct = default)
		=> ExternalTool.RunAsync("git", [Verb(operation.Kind), "--continue"], operation.WorkingDirectory, ct,
			// The operation is being driven from a UI with nowhere to show an editor, so the
			// message git would open one for is accepted as it stands.
			env: new Dictionary<string, string> { ["GIT_EDITOR"] = "true" });

	/// <summary>Drops the step it stopped at and carries on with the rest.</summary>
	public Task SkipAsync(InProgressOperation operation, CancellationToken ct = default)
		=> ExternalTool.RunAsync("git", [Verb(operation.Kind), "--skip"], operation.WorkingDirectory, ct,
			env: new Dictionary<string, string> { ["GIT_EDITOR"] = "true" });

	static string Verb(GitOperation kind) => kind switch {
		GitOperation.Rebase => "rebase",
		GitOperation.Merge => "merge",
		GitOperation.CherryPick => "cherry-pick",
		GitOperation.Revert => "revert",
		_ => "bisect",
	};

	/// <summary>
	/// Runs the user's merge tool over each conflicted step and continues the rebase, until
	/// it finishes or the tool leaves something unresolved (the user closed it without
	/// deciding, or none is configured). True only when the rebase ran to completion.
	/// </summary>
	async Task<bool> ResolveConflictsAsync(string dir, IProgress<string>? progress, CancellationToken ct)
	{
		// Which tool, by name, because the answer to "why is nothing happening" is usually
		// either that it is a window that did not come to the front or that there is none.
		string tool = "";
		try
		{
			tool = (await ExternalTool.RunAsync("git", ["config", "merge.tool"], dir, ct,
				okExitCodes: [1])).Trim();
		}
		catch (ToolFailedException)
		{
			// Not configured is an answer, not a failure; the message below says so.
		}
		string named = tool.Length > 0 ? $"'{tool}'" : "your merge tool";

		// A rebase stops once per conflicting commit, so this is a loop, not one pass. The
		// bound is a backstop against a tool that exits without ever resolving anything.
		for (int step = 0; step < 50; step++)
		{
			// The paths the tool is about to be given, so what it leaves behind can be checked.
			var conflicted = await UnmergedPathsAsync(dir, ct);
			progress?.Report(tool.Length == 0
				? "Conflicted, and no merge tool is configured (git config merge.tool). "
					+ "The rebase will be left in progress."
				: $"Conflicted - waiting for {named} to resolve conflict {step + 1}. "
					+ "It runs as a separate window and may not come to the front.");
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
			// git marks a file resolved when the tool exits without saying otherwise - for most
			// tools that means "the file was touched", which an editor closed without a decision
			// also does. Continuing on that word commits the markers themselves, and the rebase
			// reports success. What the file says is the only thing that cannot be faked.
			if (await WithConflictMarkersAsync(dir, conflicted, ct) is { Count: > 0 } unresolved)
			{
				progress?.Report($"{named} left conflict markers in {string.Join(", ", unresolved.Take(3))}"
					+ (unresolved.Count > 3 ? $" and {unresolved.Count - 3} more" : "")
					+ ". The rebase is left in progress rather than committing them.");
				return false;
			}
			progress?.Report($"Conflict {step + 1} resolved; continuing the rebase...");
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
		string name = await GetRemoteAsync(ct);
		string? pushed = await TryRevParseAsync($"refs/remotes/{name}/{branch}", ct);
		if (pushed is not null && string.Equals(pushed, local, StringComparison.OrdinalIgnoreCase))
			return new PushResult(PushOutcome.AlreadyUpToDate, local);
		bool fastForward = pushed is null || await GetMergeBaseAsync(pushed, local, ct) == pushed;
		if (fastForward)
			await RunAsync(ct, "push", name, branch);
		else
			await RunAsync(ct, "push", "--force-with-lease", name, branch);
		return new PushResult(
			pushed is null ? PushOutcome.Created : fastForward ? PushOutcome.Pushed : PushOutcome.ForcePushed,
			local);
	}

	/// <summary>The checkout that has this branch, if any. A branch can be in only one.</summary>
	async Task<WorktreeCheckout?> FindCheckoutAsync(string branch, CancellationToken ct)
		=> (await ListWorktreesAsync(ct)).FirstOrDefault(w => w.Branch == branch);

	/// <summary>
	/// Everything git has half-finished in this clone, in any of its checkouts. A rebase that
	/// stopped on a conflict is the one this tool can leave behind itself, but a reader who
	/// merged by hand in their own checkout is stuck in exactly the same way, so every checkout
	/// git knows about is asked rather than only the ones we made.
	/// </summary>
	public async Task<IReadOnlyList<InProgressOperation>> ListInProgressAsync(CancellationToken ct = default)
	{
		var found = new List<InProgressOperation>();
		foreach (var checkout in await ListWorktreesAsync(ct))
		{
			// The admin directory, not the working tree: a linked worktree keeps its
			// half-finished state under .git/worktrees/<name>, not beside its files. A worktree
			// whose directory is gone is git's to prune, not ours to report.
			if (await InProgressInAsync(checkout.Path, ct) is not { } kind)
				continue;
			bool scratch = Path.GetFileName(checkout.Path.TrimEnd(Path.DirectorySeparatorChar))
				.StartsWith("stampeded-rebase-", StringComparison.Ordinal);
			// A rebase detaches HEAD while it runs, so the worktree listing reports no branch
			// for exactly the checkout that has one at stake. Git wrote it down; read it.
			string? branch = checkout.Branch ?? await RebasingBranchAsync(checkout.Path, ct);
			found.Add(new InProgressOperation(kind, checkout.Path, branch, scratch,
				await CountUnmergedAsync(checkout.Path, ct)));
		}
		return found;
	}

	/// <summary>
	/// Where a checkout keeps its half-finished state, without asking git: a repository with
	/// forty worktrees is a repository where one process per worktree is the difference between
	/// a check that can run whenever the window is focused and one that cannot. The main
	/// worktree has .git as a directory; a linked one has it as a file naming the real place.
	/// </summary>
	static async Task<string?> AdminDirectoryAsync(string workingDirectory, CancellationToken ct)
	{
		try
		{
			string dotGit = Path.Combine(workingDirectory, ".git");
			if (Directory.Exists(dotGit))
				return dotGit;
			if (!File.Exists(dotGit))
				return null;
			string text = (await File.ReadAllTextAsync(dotGit, ct)).Trim();
			const string Marker = "gitdir:";
			if (!text.StartsWith(Marker, StringComparison.Ordinal))
				return null;
			string path = text[Marker.Length..].Trim();
			return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(workingDirectory, path));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			// A worktree whose directory is gone is git's to prune, not ours to report.
			return null;
		}
	}

	/// <summary>The branch a rebase in this checkout is rebasing, which git keeps in the state
	/// directory because HEAD itself is detached for the duration.</summary>
	async Task<string?> RebasingBranchAsync(string workingDirectory, CancellationToken ct)
	{
		try
		{
			if (await AdminDirectoryAsync(workingDirectory, ct) is not { } admin)
				return null;
			foreach (string state in new[] { "rebase-merge", "rebase-apply" })
			{
				string file = Path.Combine(admin, state, "head-name");
				if (File.Exists(file))
				{
					string name = (await File.ReadAllTextAsync(file, ct)).Trim();
					return name.StartsWith("refs/heads/", StringComparison.Ordinal)
						? name["refs/heads/".Length..] : name;
				}
			}
		}
		catch (Exception e) when (e is ToolFailedException or IOException)
		{
			// The state is git's to keep; not being able to read it only costs the name.
		}
		return null;
	}

	/// <summary>What git has half-finished in one checkout, or null when it is idle.</summary>
	public async Task<GitOperation?> InProgressInAsync(string workingDirectory, CancellationToken ct = default)
	{
		string? admin = await AdminDirectoryAsync(workingDirectory, ct);
		return admin is null ? null : FindOperation(admin);

		static GitOperation? FindOperation(string admin)
		{
			if (Directory.Exists(Path.Combine(admin, "rebase-merge"))
				|| Directory.Exists(Path.Combine(admin, "rebase-apply")))
				return GitOperation.Rebase;
			if (File.Exists(Path.Combine(admin, "MERGE_HEAD")))
				return GitOperation.Merge;
			if (File.Exists(Path.Combine(admin, "CHERRY_PICK_HEAD")))
				return GitOperation.CherryPick;
			if (File.Exists(Path.Combine(admin, "REVERT_HEAD")))
				return GitOperation.Revert;
			if (File.Exists(Path.Combine(admin, "BISECT_LOG")))
				return GitOperation.Bisect;
			return null;
		}
	}


	/// <summary>
	/// Abandons a half-finished operation, putting the checkout back where it started. A
	/// checkout that existed only to carry the operation goes with it: leaving it behind is
	/// what turns one conflicted rebase into a directory nobody remembers making.
	/// </summary>
	public async Task AbortAsync(InProgressOperation operation, CancellationToken ct = default)
	{
		string[] args = operation.Kind is GitOperation.Bisect
			? ["bisect", "reset"] : [Verb(operation.Kind), "--abort"];
		await ExternalTool.RunAsync("git", args, operation.WorkingDirectory, ct);
		if (!operation.IsScratch)
			return;
		try
		{
			await RunAsync(CancellationToken.None, "worktree", "remove", "--force", operation.WorkingDirectory);
		}
		catch (ToolFailedException)
		{
			await RunAsync(CancellationToken.None, "worktree", "prune");
		}
	}

	/// <summary>The account the remote belongs to on GitHub, or null when there is no remote or
	/// it is not a GitHub one. This is who the local branches belong to: they are pushed to
	/// that remote, so a pull request whose head repository has another owner is from a fork
	/// and names a branch that is not one of these.</summary>
	public async Task<string?> GetOriginOwnerAsync(CancellationToken ct = default)
	{
		try
		{
			string url = (await RunAsync(ct, "remote", "get-url", await GetRemoteAsync(ct))).Trim();
			return GitHub.GitHubUrl.TryParse(url, out string owner, out _, out _) ? owner : null;
		}
		catch (Infra.ToolFailedException)
		{
			return null;
		}
	}

	/// <summary>The review base for local branches: the remote's default branch when known,
	/// else its master.</summary>
	public async Task<string> GetDefaultBaseAsync(CancellationToken ct = default)
	{
		string name = await GetRemoteAsync(ct);
		try
		{
			return (await RunAsync(ct, "rev-parse", "--abbrev-ref", $"{name}/HEAD")).Trim();
		}
		catch (Infra.ToolFailedException)
		{
			return $"{name}/master";
		}
	}
}
