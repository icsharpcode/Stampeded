namespace Stampeded.Core.Testing;

/// <summary>
/// Which solution of a checkout a dotnet command should be pointed at.
///
/// A repository root with more than one solution file is not unusual - a product solution, an
/// installer, an extension - and dotnet refuses to guess between them ("MSB1011"). Naming one
/// is the whole fix, and the choice belongs in one place: the tests and the generated-sources
/// build were picking separately, which is two rules to keep in step.
/// </summary>
public static class SolutionTarget
{
	/// <summary>
	/// The file name to pass, or null when the checkout has none and dotnet can work it out
	/// alone. A cross-platform solution filter wins off Windows: a full solution usually holds
	/// projects that cannot build there - net472 add-ins, Windows-only test hosts - and the
	/// filter is the repository's own statement of what does.
	/// </summary>
	public static string? ForRoot(string root)
	{
		if (!Directory.Exists(root))
			return null;
		if (!OperatingSystem.IsWindows()
			&& Files(root, "*.slnf").FirstOrDefault(f => f.Contains("xplat", StringComparison.OrdinalIgnoreCase))
				is { } crossPlatform)
		{
			return crossPlatform;
		}
		// The largest is the product's own: an installer or extension solution holds a project
		// or two, where the one worth building holds the repository.
		return Largest(root, "*.sln") ?? Largest(root, "*.slnx");
	}

	static IEnumerable<string> Files(string root, string pattern)
		=> Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.OfType<string>()
			.Order(StringComparer.Ordinal);

	static string? Largest(string root, string pattern)
		=> Files(root, pattern)
			.OrderByDescending(name => new FileInfo(Path.Combine(root, name)).Length)
			.FirstOrDefault();
}
