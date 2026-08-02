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
