using Stampeded.Core.Diff;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Git;

/// <summary>
/// Git access for one local clone, via the git CLI. Never touches the user's working
/// tree or index: everything reads from the object database (fetch, merge-base, diff,
/// show), so an open review cannot disturb whatever the user has checked out.
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
			"--format=%(refname:short)%09%(committerdate:short)%09%(subject)"));

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
