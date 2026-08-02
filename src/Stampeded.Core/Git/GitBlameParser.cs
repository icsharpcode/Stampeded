namespace Stampeded.Core.Git;

public sealed record BlameLine(int FinalLine, string Sha, string Author, DateTimeOffset AuthorTime, string Summary);

/// <summary>Parses `git blame --porcelain` output.</summary>
public static class GitBlameParser
{
	sealed class CommitInfo
	{
		public string Author = "";
		public DateTimeOffset AuthorTime;
		public string Summary = "";
	}

	public static IReadOnlyList<BlameLine> Parse(string porcelain)
	{
		var lines = new List<BlameLine>();
		var commits = new Dictionary<string, CommitInfo>();
		CommitInfo? current = null;
		string currentSha = "";
		int finalLine = 0;

		foreach (var line in porcelain.ReplaceLineEndings("\n").Split('\n'))
		{
			if (line.StartsWith('\t'))
			{
				// Content line: closes the entry started by the last header line.
				if (current is not null)
					lines.Add(new BlameLine(finalLine, currentSha, current.Author, current.AuthorTime, current.Summary));
				continue;
			}
			if (line.Length == 0)
				continue;

			int space = line.IndexOf(' ');
			string first = space < 0 ? line : line[..space];
			if (first.Length == 40 && first.All(Uri.IsHexDigit))
			{
				// "<sha> <origLine> <finalLine> [<groupLines>]" - headers follow only on
				// the commit's first occurrence; later ones reuse the cached info.
				currentSha = first;
				var parts = line.Split(' ');
				finalLine = int.Parse(parts[2]);
				if (!commits.TryGetValue(currentSha, out current))
				{
					current = new CommitInfo();
					commits[currentSha] = current;
				}
			}
			else if (current is not null)
			{
				if (line.StartsWith("author ", StringComparison.Ordinal))
					current.Author = line["author ".Length..];
				else if (line.StartsWith("author-time ", StringComparison.Ordinal))
					current.AuthorTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(line["author-time ".Length..]));
				else if (line.StartsWith("summary ", StringComparison.Ordinal))
					current.Summary = line["summary ".Length..];
			}
		}
		return lines;
	}
}
