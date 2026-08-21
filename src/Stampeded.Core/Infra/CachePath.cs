namespace Stampeded.Core.Infra;

/// <summary>
/// Where this tool keeps things it can rebuild: worktrees of a reviewed commit, what GitHub
/// said about a pull request, a language server it installed for itself. Deleting any of it
/// costs time on the next review and nothing else.
/// </summary>
public static class CachePath
{
	/// <summary>XDG_CACHE_HOME with the standard fallbacks; SpecialFolder has no reliable
	/// cache mapping across environments (it can resolve to an empty string, which would turn
	/// the cache path relative).</summary>
	public static string For(string kind)
	{
		string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
		string root = !string.IsNullOrEmpty(xdg)
			? xdg
			: OperatingSystem.IsWindows()
				? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
				: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
		return Path.Combine(root, "stampeded", kind);
	}
}
