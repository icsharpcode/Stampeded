namespace Stampeded.Core.Git;

/// <summary><paramref name="Parents"/> is what the commit was written on top of, first one
/// first - the mainline for a merge. Carried with the commit because asking git for it one
/// commit at a time is a process each, and reading a series asks for every one of them.</summary>
public sealed record CommitInfo(string Sha, string ShortSha, string Author, string Date, string Subject,
	string Body = "", string Parents = "")
{
	/// <summary>The message as it was written: subject, blank line, body.</summary>
	public string Message => Body.Length == 0 ? Subject : $"{Subject}\n\n{Body}";

	/// <summary>The commit this one is read against: its first parent, or null for a root.</summary>
	public string? FirstParent
		=> Parents.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] ? first : null;

	/// <summary>
	/// The pending working tree rather than a commit: it has no SHA because nothing has
	/// recorded it yet. A review of a checkout that has uncommitted work reads it as one more
	/// entry of the series, sitting on top of every commit below it, so that a reader stepping
	/// through the change reaches the part nobody else can see yet.
	/// </summary>
	public bool IsWorkingTree => Sha.Length == 0;

	/// <summary>The working tree as an entry of a commit series.</summary>
	public static CommitInfo WorkingTree { get; } =
		new("", "uncommitted", "", "", "the work in your checkout, not committed yet");
}

public sealed record BranchInfo(string Name, string Sha, string Date, string Subject);

/// <summary>Parses `git log` output in the tool's tab-separated format, and
/// `git diff --name-status` output.</summary>
public static partial class GitLogParser
{
	[System.Text.RegularExpressions.GeneratedRegex(@"(\d+) insertion")]
	private static partial System.Text.RegularExpressions.Regex Insertions();

	[System.Text.RegularExpressions.GeneratedRegex(@"(\d+) deletion")]
	private static partial System.Text.RegularExpressions.Regex Deletions();

	/// <summary>
	/// Lines added and removed per commit, from `git log --format=%H --shortstat`: a full SHA
	/// on a line of its own, then git's summary of that commit. A commit that changed nothing
	/// prints no summary and is absent from the result, which is what "no lines" means here.
	/// </summary>
	public static IReadOnlyDictionary<string, (int Added, int Removed)> ParseShortStat(string output)
	{
		var stats = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
		string? sha = null;
		foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
		{
			string trimmed = line.Trim();
			if (trimmed.Length == 40 && trimmed.All(char.IsAsciiHexDigit))
			{
				sha = trimmed;
			}
			else if (sha is not null && trimmed.Contains("changed", StringComparison.Ordinal))
			{
				var insertions = Insertions().Match(trimmed);
				var deletions = Deletions().Match(trimmed);
				stats[sha] = (
					insertions.Success ? int.Parse(insertions.Groups[1].Value) : 0,
					deletions.Success ? int.Parse(deletions.Groups[1].Value) : 0);
			}
		}
		return stats;
	}

	/// <summary>
	/// How many commits touched each path, from `git log --name-only --format=` - one path per
	/// line, once per commit that changed it. Churn correlates with defect density, so this is
	/// the count a triage reads as "how often does this file go wrong".
	/// </summary>
	public static IReadOnlyDictionary<string, int> CountPathTouches(string output)
	{
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var line in output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
			counts[line] = counts.GetValueOrDefault(line) + 1;
		return counts;
	}

	/// <summary>
	/// Parses one record per commit, NUL-terminated: a header line of six tab-separated
	/// fields, then the body until the NUL. The body is the only multi-line field, which is
	/// why it needs a terminator no commit message can contain - and why it comes after the
	/// newline rather than after a tab, so a subject with tabs in it stays whole. The subject
	/// is last on that line for the same reason: only the separators before it split.
	/// </summary>
	public static IReadOnlyList<CommitInfo> Parse(string output)
	{
		var commits = new List<CommitInfo>();
		foreach (var record in output.ReplaceLineEndings("\n").Split('\0'))
		{
			var trimmed = record.TrimStart('\n');
			if (trimmed.Length == 0)
				continue;
			int firstBreak = trimmed.IndexOf('\n');
			string header = firstBreak < 0 ? trimmed : trimmed[..firstBreak];
			string body = firstBreak < 0 ? "" : trimmed[(firstBreak + 1)..].TrimEnd('\n');
			var parts = header.Split('\t', 6);
			if (parts.Length < 6)
				continue;
			commits.Add(new CommitInfo(parts[0], parts[1], parts[2], parts[3], parts[5], body, parts[4]));
		}
		return commits;
	}

	/// <summary>Parses `git for-each-ref` branch output (name, sha, date, subject; tab-separated).</summary>
	public static IReadOnlyList<BranchInfo> ParseBranches(string output)
	{
		var branches = new List<BranchInfo>();
		foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
		{
			if (line.Length == 0)
				continue;
			var parts = line.Split('\t', 4);
			if (parts.Length < 4)
				continue;
			branches.Add(new BranchInfo(parts[0], parts[1], parts[2], parts[3]));
		}
		return branches;
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
