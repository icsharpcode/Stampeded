using Stampeded.Core.Diff;

namespace Stampeded.Core.Git;

/// <summary>
/// Parses `git diff` unified output (with --find-renames) into <see cref="FileDiff"/>s.
/// </summary>
public static class GitDiffParser
{
	public static IReadOnlyList<FileDiff> Parse(string diffOutput)
	{
		var files = new List<FileDiff>();
		var lines = diffOutput.ReplaceLineEndings("\n").Split('\n');
		int i = 0;
		while (i < lines.Length)
		{
			if (!lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
			{
				i++;
				continue;
			}
			files.Add(ParseFile(lines, ref i));
		}
		return files;
	}

	static FileDiff ParseFile(string[] lines, ref int i)
	{
		// "diff --git a/<old> b/<new>" is authoritative only for equal paths; renames are
		// taken from the explicit "rename from/to" lines, adds/deletes from "---"/"+++",
		// so paths containing spaces never need to be split heuristically here.
		string? oldPath = null, newPath = null;
		var kind = FileChangeKind.Modified;
		bool isBinary = false;
		var hunks = new List<DiffHunk>();

		i++; // past "diff --git"
		for (; i < lines.Length && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal); i++)
		{
			string line = lines[i];
			if (line.StartsWith("rename from ", StringComparison.Ordinal))
			{
				kind = FileChangeKind.Renamed;
				oldPath = line["rename from ".Length..];
			}
			else if (line.StartsWith("rename to ", StringComparison.Ordinal))
			{
				newPath = line["rename to ".Length..];
			}
			else if (line.StartsWith("new file mode", StringComparison.Ordinal))
			{
				kind = FileChangeKind.Added;
			}
			else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
			{
				kind = FileChangeKind.Deleted;
			}
			else if (line.StartsWith("--- ", StringComparison.Ordinal))
			{
				string p = line[4..];
				if (p != "/dev/null")
					oldPath ??= StripPrefix(p);
			}
			else if (line.StartsWith("+++ ", StringComparison.Ordinal))
			{
				string p = line[4..];
				if (p != "/dev/null")
					newPath ??= StripPrefix(p);
			}
			else if (line.StartsWith("Binary files ", StringComparison.Ordinal))
			{
				isBinary = true;
				// "Binary files /dev/null and b/<path> differ" is the only path source
				// for binary adds/deletes (no ---/+++ lines are emitted).
				var parts = line["Binary files ".Length..^" differ".Length].Split(" and ");
				if (parts.Length == 2)
				{
					if (parts[0] != "/dev/null")
						oldPath ??= StripPrefix(parts[0]);
					if (parts[1] != "/dev/null")
						newPath ??= StripPrefix(parts[1]);
				}
			}
			else if (line.StartsWith("@@ ", StringComparison.Ordinal))
			{
				hunks.Add(ParseHunk(lines, ref i));
				i--; // ParseHunk leaves i one past its last line; the for loop advances again
			}
		}

		return new FileDiff(
			oldPath ?? newPath ?? "",
			newPath ?? oldPath ?? "",
			kind, isBinary, hunks);
	}

	static string StripPrefix(string path)
		=> path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal)
			? path[2..]
			: path;

	static DiffHunk ParseHunk(string[] lines, ref int i)
	{
		// @@ -oldStart[,oldLen] +newStart[,newLen] @@ header
		string header = lines[i];
		int secondAt = header.IndexOf(" @@", 3, StringComparison.Ordinal);
		string ranges = header[3..secondAt];
		string trailing = header.Length > secondAt + 3 ? header[(secondAt + 3)..].TrimStart() : "";

		var parts = ranges.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		var (oldStart, oldLen) = ParseRange(parts[0][1..]); // skip '-'
		var (newStart, newLen) = ParseRange(parts[1][1..]); // skip '+'

		// Content is bounded by the header's line counts, never by sniffing the next
		// header: that keeps trailing blank lines and any '+'/'-'-looking content
		// unambiguous.
		var patchLines = new List<PatchLine>();
		int oldRemaining = oldLen, newRemaining = newLen;
		i++;
		while (i < lines.Length && (oldRemaining > 0 || newRemaining > 0))
		{
			string line = lines[i];
			i++;
			if (line.StartsWith('\\'))
				continue; // "\ No newline at end of file" - metadata, not content
			if (line.StartsWith('+'))
			{
				patchLines.Add(new PatchLine(PatchLineKind.Added, line[1..]));
				newRemaining--;
			}
			else if (line.StartsWith('-'))
			{
				patchLines.Add(new PatchLine(PatchLineKind.Removed, line[1..]));
				oldRemaining--;
			}
			else
			{
				// Context: " <text>", or a completely empty line (some transports strip
				// the lone space git emits for empty context lines).
				patchLines.Add(new PatchLine(PatchLineKind.Context, line.Length > 0 ? line[1..] : ""));
				oldRemaining--;
				newRemaining--;
			}
		}
		return new DiffHunk(oldStart, oldLen, newStart, newLen, trailing, patchLines);
	}

	static (int start, int length) ParseRange(string range)
	{
		int comma = range.IndexOf(',');
		return comma < 0
			? (int.Parse(range), 1)
			: (int.Parse(range[..comma]), int.Parse(range[(comma + 1)..]));
	}
}
