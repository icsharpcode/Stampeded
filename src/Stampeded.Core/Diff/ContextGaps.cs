namespace Stampeded.Core.Diff;

/// <summary>
/// A run of unchanged lines hidden between hunks, in 1-based document lines. The control the
/// reader sees sits on <see cref="FirstLine"/>; the lines hidden behind it are FirstLine
/// through LastLine.
/// </summary>
public sealed record ContextGap(int FirstLine, int LastLine)
{
	public int HiddenCount => LastLine - FirstLine + 1;

	public bool Contains(int line) => line >= FirstLine && line <= LastLine;
}

/// <summary>
/// Which unchanged lines a diff hides, and how a reader opens them.
///
/// This is deliberately not folding. Folds are the code's own structure - types, members,
/// #regions - and a reader collapses and expands them for reasons that have nothing to do
/// with the diff. Hiding context with the same mechanism made the two fight: expanding a
/// method to read it also unhid unrelated context, "collapse all" swallowed the change, and
/// the two kinds of region cannot always nest.
/// </summary>
public static class ContextGaps
{
	/// <summary>Lines of unchanged context left visible on each side of a hunk.</summary>
	public const int Context = 3;

	/// <summary>Lines revealed per step, as GitHub's expanders do it.</summary>
	public const int Step = 20;

	/// <summary>
	/// The gaps of a diff, closed. Nothing is hidden in a document without changes, which is
	/// how a plain source view stays whole.
	/// </summary>
	public static List<ContextGap> Compute(IReadOnlyList<DiffLineTag> tags, bool hasChanges)
	{
		var gaps = new List<ContextGap>();
		if (!hasChanges)
			return gaps;
		int runStart = -1;
		for (int i = 0; i <= tags.Count; i++)
		{
			bool context = i < tags.Count && tags[i].Kind == DiffLineKind.Context;
			if (context && runStart < 0)
			{
				runStart = i;
			}
			else if (!context && runStart >= 0)
			{
				Add(gaps, tags.Count, runStart, i - 1);
				runStart = -1;
			}
		}
		return gaps;
	}

	static void Add(List<ContextGap> gaps, int tagCount, int firstTag, int lastTag)
	{
		// Context lines stay visible on each side of a hunk; at the document's edges the run
		// has a hunk on one side only, so it hides all the way to the edge.
		int first = firstTag == 0 ? firstTag : firstTag + Context;
		int last = lastTag == tagCount - 1 ? lastTag : lastTag - Context;
		// A single hidden line is worth less than the control that would hide it.
		if (last - first + 1 < 2)
			return;
		gaps.Add(new ContextGap(first + 1, last + 1));
	}

	/// <summary>
	/// Reveals up to <paramref name="step"/> lines at the top of a gap - the ones that follow
	/// the hunk above it. Null when that opens the gap completely.
	/// </summary>
	public static ContextGap? RevealTop(ContextGap gap, int step)
		=> gap.HiddenCount <= step ? null : gap with { FirstLine = gap.FirstLine + step };

	/// <summary>
	/// Reveals up to <paramref name="step"/> lines at the bottom of a gap - the ones that
	/// lead into the hunk below it. Null when that opens the gap completely.
	/// </summary>
	public static ContextGap? RevealBottom(ContextGap gap, int step)
		=> gap.HiddenCount <= step ? null : gap with { LastLine = gap.LastLine - step };
}
