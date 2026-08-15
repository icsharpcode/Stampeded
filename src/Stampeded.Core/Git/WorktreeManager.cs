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
			var keep = keepShas.Select(s => s.Length > 9 ? s[..9] : s).ToHashSet();
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

	/// <summary>
	/// Keeps the named worktrees and the <paramref name="recent"/> most recently used others,
	/// and deletes the rest. A worktree is a copy of the whole tree plus what building it
	/// leaves behind, and one is made for every revision ever reviewed - left alone they are
	/// the largest thing this tool puts on a disk, and the ones worth keeping are the few a
	/// reader comes back to.
	///
	/// A directory in use by something outside this process - an editor opened on it, a test
	/// run, a launched application - keeps working after its deletion on the platforms that
	/// allow it, and is recreated the next time it is asked for.
	/// </summary>
	public async Task<int> PruneToRecentAsync(
		IReadOnlyCollection<string> keepShas, int recent, CancellationToken ct = default)
	{
		string repoDir = Path.Combine(CacheRoot, Path.GetFileName(repoPath));
		if (!Directory.Exists(repoDir))
			return 0;
		var pinned = keepShas.Select(s => s[..9]).ToHashSet();
		var survivors = Directory.EnumerateDirectories(repoDir)
			.Where(d => !pinned.Contains(Path.GetFileName(d)))
			.OrderByDescending(Directory.GetLastWriteTimeUtc)
			.Take(recent)
			.Select(Path.GetFileName)
			.OfType<string>();
		return await PruneAsync([.. pinned, .. survivors], ct);
	}

	public async Task<string> GetOrCreateAsync(string sha, CancellationToken ct = default)
	{
		string dir = Path.GetFullPath(Path.Combine(CacheRoot, Path.GetFileName(repoPath), sha[..9]));
		if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, ".git")))
		{
			// Reuse counts as use: what the cache keeps is what a reader comes back to, not
			// what was built in most recently.
			Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
			LinkSubmodulesFromSource(dir);
			return dir;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
		// A stale registration for a deleted directory blocks re-adding; prune first.
		await ExternalTool.RunAsync("git", ["worktree", "prune"], repoPath, ct);
		await ExternalTool.RunAsync("git", ["worktree", "add", "--detach", dir, sha], repoPath, ct);
		LinkSubmodulesFromSource(dir);
		return dir;
	}

	/// <summary>
	/// `git worktree add` leaves submodules as empty stubs; share the source clone's
	/// checkouts via symlinks so worktree test runs find their fixtures (e.g. ILSpy's
	/// heavyweight ILSpy-tests submodule with its offline nuget cache).
	/// </summary>
	void LinkSubmodulesFromSource(string worktreeDir)
	{
		string gitmodules = Path.Combine(worktreeDir, ".gitmodules");
		if (!File.Exists(gitmodules))
			return;
		foreach (var line in File.ReadAllLines(gitmodules))
		{
			string trimmed = line.Trim();
			if (!trimmed.StartsWith("path", StringComparison.Ordinal))
				continue;
			int eq = trimmed.IndexOf('=');
			if (eq < 0)
				continue;
			string rel = trimmed[(eq + 1)..].Trim();
			string source = Path.Combine(repoPath, rel);
			string target = Path.Combine(worktreeDir, rel);
			bool sourcePopulated = Directory.Exists(source) && Directory.EnumerateFileSystemEntries(source).Any();
			bool targetPopulated = (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
				|| File.Exists(target); // already a symlink or file
			if (!sourcePopulated || targetPopulated)
				continue;
			try
			{
				if (Directory.Exists(target))
					Directory.Delete(target);
				Directory.CreateSymbolicLink(target, source);
				CliLog.Write("worktree", $"linked submodule {rel} from source clone");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				CliLog.Write("worktree", $"could not link submodule {rel}: {ex.Message}");
			}
		}
	}
}
