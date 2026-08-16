namespace Stampeded.Core.Review;

/// <summary>
/// A position anchor for review comments that survives force-pushes: the line's text plus
/// a small context window, re-attached by content rather than line number.
/// </summary>
public sealed record CommentAnchor(
	string Path,
	bool OldSide,
	int Line,
	string LineText,
	IReadOnlyList<string> ContextBefore,
	IReadOnlyList<string> ContextAfter)
{
	/// <summary>Text matches farther than this from the original line don't count as the
	/// same location once their context is gone.</summary>
	const int FuzzRange = 20;

	/// <summary>Best-effort location when <see cref="Reattach"/> failed (the line itself
	/// is gone): the position whose surviving non-blank context lines best surround it,
	/// falling back to the original line number clamped into the file. Never null - the
	/// caller decides whether an approximation is acceptable.</summary>
	public int Approximate(IReadOnlyList<string> fileLines)
	{
		int bestScore = 0, bestLine = 1, bestDistance = int.MaxValue;
		for (int ghost = 0; ghost <= fileLines.Count; ghost++)
		{
			int score = 0;
			for (int k = 0; k < ContextBefore.Count; k++)
			{
				int want = ghost - ContextBefore.Count + k;
				if (want >= 0 && want < fileLines.Count
					&& !string.IsNullOrWhiteSpace(ContextBefore[k]) && fileLines[want] == ContextBefore[k])
					score++;
			}
			for (int k = 0; k < ContextAfter.Count; k++)
			{
				int want = ghost + k;
				if (want < fileLines.Count
					&& !string.IsNullOrWhiteSpace(ContextAfter[k]) && fileLines[want] == ContextAfter[k])
					score++;
			}
			int distance = Math.Abs(ghost + 1 - Line);
			if (score > bestScore || (score == bestScore && score > 0 && distance < bestDistance))
			{
				bestScore = score;
				bestLine = Math.Max(1, ghost);
				bestDistance = distance;
			}
		}
		return bestScore > 0 ? bestLine : Math.Clamp(Line, 1, Math.Max(1, fileLines.Count));
	}

	/// <summary>
	/// The anchor a posted comment carries with it. GitHub drops a comment's line number once
	/// the diff has moved on, but keeps the excerpt it was written against, and that excerpt
	/// ends at the commented line - so the last line of the side the comment is on is the line
	/// itself, and what comes before it is its context. Null when the excerpt holds nothing of
	/// that side at all.
	/// </summary>
	public static CommentAnchor? FromDiffHunk(string path, bool oldSide, int originalLine, string diffHunk)
	{
		var sideLines = new List<string>();
		foreach (var hunkLine in diffHunk.ReplaceLineEndings("\n").Split('\n'))
		{
			if (hunkLine.StartsWith("@@", StringComparison.Ordinal))
				continue;
			char marker = hunkLine.Length > 0 ? hunkLine[0] : ' ';
			if (marker == ' ' || (oldSide ? marker == '-' : marker == '+'))
				sideLines.Add(hunkLine.Length > 0 ? hunkLine[1..] : "");
		}
		if (sideLines.Count == 0)
			return null;
		var before = sideLines.Count > 1
			? sideLines.GetRange(Math.Max(0, sideLines.Count - 3), Math.Min(2, sideLines.Count - 1))
			: [];
		return new CommentAnchor(path, oldSide, originalLine, sideLines[^1], before, []);
	}

	public static CommentAnchor Create(string path, bool oldSide, int line, IReadOnlyList<string> fileLines, int context = 2)
	{
		int index = line - 1;
		var before = new List<string>();
		for (int i = Math.Max(0, index - context); i < index; i++)
			before.Add(fileLines[i]);
		var after = new List<string>();
		for (int i = index + 1; i < Math.Min(fileLines.Count, index + 1 + context); i++)
			after.Add(fileLines[i]);
		return new CommentAnchor(path, oldSide, line, fileLines[index], before, after);
	}

	/// <summary>
	/// 1-based line in <paramref name="fileLines"/> this anchor names now, or null when the
	/// line no longer exists (Outdated). Exact stage: line text plus full context window;
	/// fuzzy stage: line text alone within <see cref="FuzzRange"/> of the original line.
	/// </summary>
	public int? Reattach(IReadOnlyList<string> fileLines)
	{
		var textMatches = new List<int>();
		for (int i = 0; i < fileLines.Count; i++)
		{
			if (fileLines[i] == LineText)
				textMatches.Add(i + 1);
		}
		if (textMatches.Count == 0)
			return null;

		int ContextScore(int line)
		{
			int index = line - 1;
			int score = 0;
			for (int k = 0; k < ContextBefore.Count; k++)
			{
				int want = index - ContextBefore.Count + k;
				if (want >= 0 && want < fileLines.Count && fileLines[want] == ContextBefore[k])
					score++;
			}
			for (int k = 0; k < ContextAfter.Count; k++)
			{
				int want = index + 1 + k;
				if (want < fileLines.Count && fileLines[want] == ContextAfter[k])
					score++;
			}
			return score;
		}

		int fullScore = ContextBefore.Count + ContextAfter.Count;
		int best = textMatches
			.OrderByDescending(ContextScore)
			.ThenBy(l => Math.Abs(l - Line))
			.First();
		if (ContextScore(best) == fullScore)
			return best;

		return textMatches
			.Where(l => Math.Abs(l - Line) <= FuzzRange)
			.OrderBy(l => Math.Abs(l - Line))
			.Cast<int?>()
			.FirstOrDefault();
	}
}
