namespace Stampeded.Core.Diff;

/// <summary>
/// Turns a unified patch as git prints it - `git show`, `git diff a b` - into the same
/// document model a review file uses, so a whole commit reads with the colouring, the
/// line-kind margin and the hunk navigation instead of as grey text with +/- in it.
///
/// The patch text is kept verbatim, prefix characters and all: the document spans many
/// files, so its lines cannot be blob lines of any one of them, and the +/- column is the
/// only thing that still says which file a line belongs to once you scroll.
/// </summary>
public static class PatchDocumentBuilder
{
	public static DiffDocumentModel Build(string patch)
	{
		var lines = patch.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
		var tags = new List<DiffLineTag>(lines.Length);
		// Outside a hunk every line is prose or a header: a commit message body is indented,
		// so ' ' and '-' there must not be read as context and removal.
		bool inHunk = false;
		int oldNo = 0, newNo = 0;

		foreach (var line in lines)
		{
			if (line.StartsWith("@@", StringComparison.Ordinal))
			{
				(oldNo, newNo) = ParseHunkStarts(line);
				inHunk = true;
				tags.Add(new DiffLineTag(DiffLineKind.Context, 0, 0, null));
				continue;
			}
			if (line.StartsWith("diff --git ", StringComparison.Ordinal))
				inHunk = false;
			if (!inHunk || line.Length == 0)
			{
				tags.Add(new DiffLineTag(DiffLineKind.Context, 0, 0, null));
				continue;
			}
			switch (line[0])
			{
				case '+':
					tags.Add(new DiffLineTag(DiffLineKind.Added, 0, newNo++, null));
					break;
				case '-':
					tags.Add(new DiffLineTag(DiffLineKind.Removed, oldNo++, 0, null));
					break;
				case ' ':
					tags.Add(new DiffLineTag(DiffLineKind.Context, oldNo++, newNo++, null));
					break;
				default:
					// "\ No newline at end of file", or trailing prose after the last hunk.
					tags.Add(new DiffLineTag(DiffLineKind.Context, 0, 0, null));
					break;
			}
		}

		return new DiffDocumentModel {
			Text = string.Join('\n', lines),
			Tags = tags,
			Hunks = DiffDocumentBuilder.ComputeHunks(tags),
		};
	}

	/// <summary>The first old and new line numbers of a `@@ -a,b +c,d @@` header; (0, 0)
	/// when it does not parse, which only costs that hunk its line numbers.</summary>
	static (int Old, int New) ParseHunkStarts(string header)
	{
		int old = 0, @new = 0;
		foreach (var part in header.Split(' '))
		{
			if (part.Length < 2 || (part[0] != '-' && part[0] != '+'))
				continue;
			var digits = part[1..];
			int comma = digits.IndexOf(',');
			if (comma >= 0)
				digits = digits[..comma];
			if (!int.TryParse(digits, out int start))
				continue;
			if (part[0] == '-')
				old = start;
			else
				@new = start;
		}
		return (old, @new);
	}
}
