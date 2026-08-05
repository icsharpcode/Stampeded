using Stampeded.Core.Diff;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Git;

/// <summary>
/// Git access for one local clone, via the git CLI. Never touches the user's working
/// tree or index: reads come from the object database (fetch, merge-base, diff, show),
/// and the operations that write (branch creation, rebase) touch refs only, running any
/// checkout they need in a throwaway worktree. An open review cannot disturb whatever
/// the user has checked out.
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

	public Task<string> ShowFileAsync(string rev, string path, CancellationToken ct = default)
		=> RunAsync(ct, "show", $"{rev}:{path}");

	public async Task<IReadOnlyList<BlameLine>> BlameAsync(string rev, string path, CancellationToken ct = default)
		=> GitBlameParser.Parse(await RunAsync(ct, "blame", "--porcelain", rev, "--", path));

	const string LogFormat = "--format=%H%x09%h%x09%an%x09%ad%x09%s";

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

	/// <summary>Local branches, most recently committed first.</summary>
	public async Task<IReadOnlyList<BranchInfo>> ListBranchesAsync(CancellationToken ct = default)
		=> GitLogParser.ParseBranches(await RunAsync(ct,
			"for-each-ref", "refs/heads", "--sort=-committerdate",
			"--format=%(refname:short)%09%(objectname)%09%(committerdate:short)%09%(subject)"));

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
	/// checkout alone. Returns the branch's SHA from before the rebase, which is the
	/// recovery point if the result is unwanted. On conflict the rebase is aborted, so the
	/// branch is left exactly as it was, and the conflict is reported as a failure.
	/// Git itself refuses when the branch is checked out somewhere, and that message is
	/// passed through unchanged.
	/// </summary>
	public async Task<string> RebaseBranchAsync(string branch, string onto, CancellationToken ct = default)
	{
		string before = await RevParseAsync(branch, ct);
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-rebase-" + Guid.NewGuid().ToString("N")[..8]);
		await RunAsync(ct, "worktree", "add", "--quiet", dir, branch);
		try
		{
			await ExternalTool.RunAsync("git", ["rebase", onto], dir, ct);
			return before;
		}
		catch (ToolFailedException)
		{
			try
			{
				await ExternalTool.RunAsync("git", ["rebase", "--abort"], dir, ct);
			}
			catch (ToolFailedException)
			{
				// Nothing to abort (the rebase failed before starting); the branch is
				// untouched either way, so the original failure is what matters.
			}
			throw;
		}
		finally
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
