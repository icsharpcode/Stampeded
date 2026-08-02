using Stampeded.Core.Infra;

namespace Stampeded.Core.Git;

/// <summary>
/// Detached worktrees for review heads, under the user cache directory so the user's own
/// checkout is never disturbed. One worktree per (repo, sha); reused when it exists.
/// </summary>
public sealed class WorktreeManager(string repoPath)
{
	// SpecialFolder.InternetCache maps to XDG_CACHE_HOME (~/.cache) on Unix.
	static string CacheRoot => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.InternetCache), "stampeded", "worktrees");

	public async Task<string> GetOrCreateAsync(string sha, CancellationToken ct = default)
	{
		string dir = Path.GetFullPath(Path.Combine(CacheRoot, Path.GetFileName(repoPath), sha[..9]));
		if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, ".git")))
			return dir;
		Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
		// A stale registration for a deleted directory blocks re-adding; prune first.
		await ExternalTool.RunAsync("git", ["worktree", "prune"], repoPath, ct);
		await ExternalTool.RunAsync("git", ["worktree", "add", "--detach", dir, sha], repoPath, ct);
		return dir;
	}
}
