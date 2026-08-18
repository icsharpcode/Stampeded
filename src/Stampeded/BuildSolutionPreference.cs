namespace Stampeded;

/// <summary>
/// Which solution this repository's builds are for, when the reader has said. Kept per
/// repository, because the answer is a fact about one: a checkout with an installer solution
/// beside the product's own has a right answer that no rule about file names can be sure of.
///
/// Unset means the automatic choice, which is what most repositories want and every repository
/// starts with.
/// </summary>
public static class BuildSolutionPreference
{
	const string FileName = "build-solutions.txt";

	/// <summary>The solution chosen for a repository, or null while the choice is automatic.</summary>
	public static string? For(string repoPath)
	{
		foreach (var (path, solution) in Read())
		{
			if (string.Equals(path, repoPath, StringComparison.Ordinal))
				return solution;
		}
		return null;
	}

	/// <summary>Names the solution to build for a repository; null puts it back to automatic.</summary>
	public static void Set(string repoPath, string? solution)
	{
		var lines = Read()
			.Where(entry => !string.Equals(entry.RepoPath, repoPath, StringComparison.Ordinal))
			.Select(entry => $"{entry.RepoPath}\t{entry.Solution}")
			.ToList();
		if (solution is { Length: > 0 })
			lines.Add($"{repoPath}\t{solution}");
		UserData.Write(FileName, string.Join('\n', lines));
	}

	static IEnumerable<(string RepoPath, string Solution)> Read()
	{
		foreach (string line in (UserData.Read(FileName) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			// Tab-separated because a path can hold anything else; a line that is not a pair is
			// a line an older or newer build wrote, and skipping it beats losing the file.
			var parts = line.Split('\t');
			if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
				yield return (parts[0], parts[1]);
		}
	}
}
