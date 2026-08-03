namespace Stampeded.Core.Git;

public sealed record CommitInfo(string Sha, string ShortSha, string Author, string Date, string Subject);

/// <summary>Parses `git log` output in the tool's tab-separated format, and
/// `git diff --name-status` output.</summary>
public static class GitLogParser
{
	public static IReadOnlyList<CommitInfo> Parse(string output)
	{
		var commits = new List<CommitInfo>();
		foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
		{
			if (line.Length == 0)
				continue;
			var parts = line.Split('\t', 5);
			if (parts.Length < 5)
				continue;
			commits.Add(new CommitInfo(parts[0], parts[1], parts[2], parts[3], parts[4]));
		}
		return commits;
	}

	public static IReadOnlyList<(char Status, string Path)> ParseNameStatus(string output)
	{
		var entries = new List<(char, string)>();
		foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
		{
			if (line.Length == 0)
				continue;
			var parts = line.Split('\t');
			if (parts.Length < 2)
				continue;
			// Renames/copies (R100, C75) carry old and new path; the last is current.
			entries.Add((parts[0][0], parts[^1]));
		}
		return entries;
	}
}
