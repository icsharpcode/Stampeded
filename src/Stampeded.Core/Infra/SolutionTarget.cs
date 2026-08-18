namespace Stampeded.Core.Infra;

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
	/// <param name="chosen">A solution named by the reader, which wins whenever the checkout
	/// still has it. The guess below is only a default: which solution a repository is worth
	/// building is a fact about the repository that nobody can derive from its file names.
	/// </param>
	public static string? ForRoot(string root, string? chosen = null)
	{
		if (!Directory.Exists(root))
			return null;
		if (chosen is { Length: > 0 } && Candidates(root).Contains(chosen, StringComparer.Ordinal))
			return chosen;
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

	/// <summary>Every solution of a checkout root, for a reader choosing between them.</summary>
	public static IReadOnlyList<string> Candidates(string root)
		=> Directory.Exists(root)
			? [.. Files(root, "*.sln").Concat(Files(root, "*.slnx")).Concat(Files(root, "*.slnf"))]
			: [];

	/// <summary>
	/// The solution to load semantics from, which is not always the one to build: Roslyn opens
	/// a solution, not a filter, so a checkout whose builds are filtered still has to compile
	/// through the solution the filter names. A filter says which that is, in its own file.
	/// </summary>
	public static string? ForSemantics(string root, string? chosen = null)
	{
		string? target = ForRoot(root, chosen);
		if (target is null || !target.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
			return target;
		if (SolutionOfFilter(Path.Combine(root, target)) is { } named
			&& File.Exists(Path.Combine(root, named)))
		{
			return named;
		}
		// A filter naming a solution nobody can find is no worse than having no filter: fall
		// back to the guess, which never answers with one.
		return ForRoot(root);
	}

	/// <summary>The solution a filter is a filter of, read from its "solution": { "path": ... }.
	/// Answers null for anything that does not parse, which is what an unreadable filter is.
	/// </summary>
	static string? SolutionOfFilter(string filterPath)
	{
		try
		{
			using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(filterPath));
			if (!document.RootElement.TryGetProperty("solution", out var solution)
				|| !solution.TryGetProperty("path", out var path)
				|| path.GetString() is not { Length: > 0 } value)
			{
				return null;
			}
			// Written with Windows separators even on repositories that never see Windows.
			return value.Replace('\\', Path.DirectorySeparatorChar);
		}
		catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
		{
			return null;
		}
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
