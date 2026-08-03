using Stampeded.Core.Diff;

namespace Stampeded.Core.Review;

public enum FileCategory
{
	Implementation,
	Test,
	Dependency,
	Generated,
}

public sealed record TriageFileRow(string Path, FileCategory Category, int Added, int Removed, int Minutes);

public sealed record TriageTotals(
	IReadOnlyList<TriageFileRow> Rows,
	int ImplChanged, int TestChanged, int GeneratedChanged, int DependencyFiles,
	int Minutes, int Sittings);

/// <summary>
/// Prices a review honestly by what the lines ARE, not just how many there are:
/// implementation is read at reasoning speed, tests mostly at scanning speed, generated
/// content barely at all. Rates are lines per minute of focused review, anchored on the
/// 200-400 lines/hour band for implementation code.
/// </summary>
public static class TriageEstimate
{
	const double ImplLinesPerMinute = 5;
	const double TestLinesPerMinute = 15;
	const double GeneratedLinesPerMinute = 50;
	const int DependencyFileMinutes = 2;
	const int MinutesPerSitting = 75;

	static readonly string[] DependencyFileHints = [".csproj", ".props", ".targets", "packages.lock.json", "global.json", ".sln", ".slnx", "nuget.config"];
	static readonly string[] GeneratedFileHints = [".g.cs", ".g.i.cs", ".designer.cs", ".generated.cs"];

	public static bool IsDependencyFile(string path)
		=> DependencyFileHints.Any(h => path.EndsWith(h, StringComparison.OrdinalIgnoreCase)
			|| System.IO.Path.GetFileName(path).Equals(h, StringComparison.OrdinalIgnoreCase));

	public static FileCategory Categorize(string path)
	{
		if (IsDependencyFile(path))
			return FileCategory.Dependency;
		if (GeneratedFileHints.Any(h => path.EndsWith(h, StringComparison.OrdinalIgnoreCase)))
			return FileCategory.Generated;
		if (TestPaths.IsTestPath(path))
			return FileCategory.Test;
		return FileCategory.Implementation;
	}

	public static TriageTotals Compute(IEnumerable<FileDiff> files)
	{
		var rows = new List<TriageFileRow>();
		int impl = 0, test = 0, generated = 0, dependencyFiles = 0;
		foreach (var file in files)
		{
			int added = file.Hunks.Sum(h => h.Lines.Count(l => l.Kind == PatchLineKind.Added));
			int removed = file.Hunks.Sum(h => h.Lines.Count(l => l.Kind == PatchLineKind.Removed));
			int changed = added + removed;
			var category = Categorize(file.Path);
			int minutes = category switch {
				FileCategory.Dependency => DependencyFileMinutes,
				FileCategory.Generated => (int)Math.Ceiling(changed / GeneratedLinesPerMinute),
				FileCategory.Test => (int)Math.Ceiling(changed / TestLinesPerMinute),
				_ => (int)Math.Ceiling(changed / ImplLinesPerMinute),
			};
			switch (category)
			{
				case FileCategory.Dependency:
					dependencyFiles++;
					break;
				case FileCategory.Generated:
					generated += changed;
					break;
				case FileCategory.Test:
					test += changed;
					break;
				default:
					impl += changed;
					break;
			}
			rows.Add(new TriageFileRow(file.Path, category, added, removed, minutes));
		}
		int totalMinutes = rows.Sum(r => r.Minutes);
		return new TriageTotals(
			rows.OrderByDescending(r => r.Minutes).ThenBy(r => r.Path, StringComparer.Ordinal).ToList(),
			impl, test, generated, dependencyFiles,
			totalMinutes,
			Math.Max(1, (totalMinutes + MinutesPerSitting - 1) / MinutesPerSitting));
	}
}
