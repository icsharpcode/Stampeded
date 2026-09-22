namespace Stampeded.Core.Infra;

/// <summary>
/// The mapping between the repo-relative paths git speaks and the absolute ones a semantic
/// provider reports. Every provider needs both directions and they have to agree exactly:
/// a path that fails to map reads as "outside the tree", which silently drops every reference
/// hit and navigation target pointing at it.
/// </summary>
public static class WorkspacePaths
{
	/// <summary>
	/// Absolute path of a repo-relative one. Git speaks forward slashes on every platform and
	/// Path.Combine only inserts a separator without touching the ones already there, so on
	/// Windows the result would keep "src/Foo.cs" while a document index is keyed on what the
	/// provider reports, "src\Foo.cs" - and every lookup would miss, taking the whole semantic
	/// layer down with it. GetFullPath normalises; elsewhere it changes nothing.
	/// </summary>
	public static string ToAbsolute(string root, string repoRelativePath)
		=> Path.GetFullPath(Path.Combine(root, repoRelativePath.Replace('/', Path.DirectorySeparatorChar)));

	/// <summary>
	/// The root-relative form of an absolute path, or null for a path outside the root.
	///
	/// Compared the way the filesystem does: on Windows a provider's spelling of a path need
	/// not match how the root was spelled, and treating that as "outside" drops the answer.
	/// Case still matters elsewhere, because two files of one tree may differ only in it.
	///
	/// The character after the root has to be a separator, or a sibling directory whose name
	/// merely starts with the root's counts as being inside it: "/repo-other/x.cs" under
	/// "/repo" would answer "-other/x.cs", a path nothing in the review has.
	/// </summary>
	public static string? ToRelative(string root, string absolutePath)
	{
		string full = Path.GetFullPath(absolutePath);
		string trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		if (full.Length <= trimmed.Length || !full.StartsWith(trimmed, comparison)
			|| (full[trimmed.Length] != Path.DirectorySeparatorChar
				&& full[trimmed.Length] != Path.AltDirectorySeparatorChar))
		{
			return null;
		}
		return full[(trimmed.Length + 1)..].Replace('\\', '/');
	}
}
