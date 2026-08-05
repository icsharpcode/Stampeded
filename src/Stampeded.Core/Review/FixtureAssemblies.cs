namespace Stampeded.Core.Review;

/// <summary>
/// ILSpy-specific: maps changed decompiler test-case sources to the fixture assemblies the
/// test suite compiles next to them (one per compiler variant, e.g. Name.opt.roslyn4.dll),
/// so a review can open exactly the affected fixtures in the head-built ILSpy UI.
/// </summary>
public static class FixtureAssemblies
{
	const string TestCasesRoot = "ICSharpCode.Decompiler.Tests/TestCases/";
	static readonly string[] SourceExtensions = [".cs", ".il", ".vb"];

	/// <summary>Distinct (relative directory, fixture name) pairs for changed fixture
	/// sources. An ILPretty fixture's .il and expected .cs collapse to one entry.</summary>
	public static IReadOnlyList<(string RelDir, string Name)> AffectedFixtures(IEnumerable<string> changedRelPaths)
	{
		var result = new List<(string, string)>();
		foreach (var path in changedRelPaths)
		{
			if (!path.StartsWith(TestCasesRoot, StringComparison.Ordinal))
				continue;
			string ext = Path.GetExtension(path);
			if (!SourceExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
				continue;
			string name = Path.GetFileName(path)[..^ext.Length];
			// Variant sources like Name.opt.roslyn.il belong to the base fixture name.
			int dot = name.IndexOf('.');
			if (dot > 0)
				name = name[..dot];
			var entry = (Path.GetDirectoryName(path)!.Replace('\\', '/'), name);
			if (!result.Contains(entry))
				result.Add(entry);
		}
		return result;
	}

	/// <summary>Whether a file is a compiled assembly of the fixture: the exact name or the
	/// name followed by a variant suffix, with a .dll/.exe extension.</summary>
	public static bool IsAssemblyOf(string fixtureName, string fileName)
	{
		string ext = Path.GetExtension(fileName);
		if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
			return false;
		string stem = fileName[..^ext.Length];
		return stem == fixtureName || stem.StartsWith(fixtureName + ".", StringComparison.Ordinal);
	}
}
