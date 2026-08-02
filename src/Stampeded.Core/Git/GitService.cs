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
}
