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
}

public sealed record BranchInfo(string Name, string Sha, string Date, string Subject);

/// <summary>Parses `git log` output in the tool's tab-separated format, and
/// `git diff --name-status` output.</summary>
public static class GitLogParser
{
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
