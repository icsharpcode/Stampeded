using Stampeded.Core.Infra;

namespace Stampeded.Core.Git;

/// <summary>
/// Detached worktrees for review heads, under the user cache directory so the user's own
/// checkout is never disturbed. One worktree per (repo, sha); reused when it exists.
/// </summary>
public sealed class WorktreeManager(string repoPath)
{
	// XDG_CACHE_HOME with the standard fallbacks; SpecialFolder has no reliable cache
	// mapping across environments (it can resolve to an empty string, which would turn
	// the cache path relative).
	static string CacheRoot {
		get {
			string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
			string root = !string.IsNullOrEmpty(xdg)
				? xdg
				: OperatingSystem.IsWindows()
					? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
					: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
			return Path.Combine(root, "stampeded", "worktrees");
		}
	}

	/// <summary>Deletes cached worktrees of this repo except those for the given SHAs,
	/// then prunes git's registrations. Returns the number of directories removed.</summary>
	public async Task<int> PruneAsync(IReadOnlyCollection<string> keepShas, CancellationToken ct = default)
	{
		string repoDir = Path.Combine(CacheRoot, Path.GetFileName(repoPath));
		int removed = 0;
		if (Directory.Exists(repoDir))
		{
			var keep = keepShas.Select(s => s[..9]).ToHashSet();
			foreach (var dir in Directory.EnumerateDirectories(repoDir))
			{
				if (keep.Contains(Path.GetFileName(dir)))
					continue;
				Directory.Delete(dir, recursive: true);
				removed++;
			}
		}
		await ExternalTool.RunAsync("git", ["worktree", "prune"], repoPath, ct);
		return removed;
	}

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
