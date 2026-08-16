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
	public const int Context = 5;

	/// <summary>Lines revealed per step, as GitHub's expanders do it.</summary>
	public const int Step = 20;

	/// <summary>
	/// How many lines a collapsed section has to hide to be worth the control that hides them.
	/// A bar standing for three lines - between a signature and the hunk under it, or between
	/// two hunks - costs the reader more attention than reading the three lines does, and a
	/// diff broken up by bars that save nothing is harder to read than the lines they replace.
	/// </summary>
	public const int MinHidden = 6;

	/// <summary>
	/// The gaps of a diff, closed. Nothing is hidden in a document without changes, which is
	/// how a plain source view stays whole.
	/// </summary>
	/// <param name="declarations">The code's structural ranges, in document lines. A run hiding
	/// the header of a declaration the change is inside is cut around that header, so a hunk is
	/// read with the lines saying what it is part of - the type, the #region, the signature -
	/// and not as a fragment of a body. Empty for anything that is not C#.</param>
	public static List<ContextGap> Compute(
		IReadOnlyList<DiffLineTag> tags, bool hasChanges, IReadOnlyList<FoldRange>? declarations = null)
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
		if (declarations is not { Count: > 0 })
			return gaps;
		var headers = Headers(tags, declarations);
		return headers.Count == 0 ? gaps : [.. gaps.SelectMany(g => Split(g, headers))];
	}

	/// <summary>
	/// The header lines of the declarations a change is inside, as 1-based inclusive ranges of
	/// document lines, in order. Containment is the whole test: a type declared five hundred
	/// lines above still says what the change is part of, while the member just above the one
	/// being changed says nothing about it however close it sits. A run below the last hunk
	/// therefore contributes nothing - a declaration starting there holds no change.
	/// </summary>
	static List<(int First, int Last)> Headers(
		IReadOnlyList<DiffLineTag> tags, IReadOnlyList<FoldRange> declarations)
	{
		// Changed lines counted once, from the front, so asking about a range is one
		// subtraction: a type's range covers most of a file, and there is one of these per
		// member.
		var changed = new int[tags.Count + 1];
		for (int i = 0; i < tags.Count; i++)
			changed[i + 1] = changed[i] + (tags[i].Kind == DiffLineKind.Context ? 0 : 1);
		var headers = new List<(int First, int Last)>();
		foreach (var range in declarations)
		{
			int first = Math.Clamp(range.StartLine, 1, tags.Count);
			int last = Math.Clamp(range.EndLine, first, tags.Count);
			if (changed[last] > changed[first - 1])
				headers.Add((first, Math.Clamp(range.HeaderEndLine, first, last)));
		}
		// The ranges arrive in the order they were parsed, with the #region folds appended last.
		headers.Sort();
		return headers;
	}

	/// <summary>
	/// One run, cut around the headers inside it: what lies above a header stays hidden, the
	/// header itself is shown, and what lies between it and the next header or the hunk is
	/// hidden only when there is enough of it to be worth a control. A run holding no header is
	/// left exactly as the scan produced it.
	/// </summary>
	static List<ContextGap> Split(ContextGap gap, List<(int First, int Last)> headers)
	{
		var pieces = new List<ContextGap>();
		int cursor = gap.FirstLine;
		foreach (var (first, last) in headers)
		{
			// A header above what is left of the run, or below its end, cuts nothing. Nested
			// declarations opened on one line - a type and the member under it - arrive as
			// ranges that touch or overlap, and the cursor merges them.
			if (last < cursor || first > gap.LastLine)
				continue;
			if (first > cursor)
				pieces.Add(new ContextGap(cursor, first - 1));
			cursor = last + 1;
		}
		if (cursor == gap.FirstLine)
			return [gap];
		if (cursor <= gap.LastLine)
			pieces.Add(new ContextGap(cursor, gap.LastLine));
		return [.. pieces.Where(p => p.HiddenCount >= MinHidden)];
	}

	static void Add(List<ContextGap> gaps, int tagCount, int firstTag, int lastTag)
	{
		// Context lines stay visible on each side of a hunk; at the document's edges the run
		// has a hunk on one side only, so it hides all the way to the edge.
		int first = firstTag == 0 ? firstTag : firstTag + Context;
		int last = lastTag == tagCount - 1 ? lastTag : lastTag - Context;
		if (last - first + 1 < MinHidden)
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
