namespace Stampeded.Core.Diff;

/// <summary>
/// A fold expressed in 1-based document lines, before it is turned into offsets against a
/// particular editor's document. The side-by-side view installs the same ranges in two
/// editors whose line lengths differ, so ranges and offsets have to stay separate.
/// </summary>
/// <param name="FromHeaderEnd">Fold from the end of the first line instead of its start,
/// so a member's signature stays visible while its body collapses.</param>
/// <param name="HeaderEndLine">Last line of the declaration itself - the one opening the body.
/// A header wrapped over several lines is one thing to read, so whatever shows a signature
/// shows all of it.</param>
public sealed record FoldRange(int StartLine, int EndLine, string Name, bool DefaultClosed, bool FromHeaderEnd,
	int HeaderEndLine);

/// <summary>
/// Structural folding for the diff views: the code's own regions, and nothing about the
/// diff. Hiding unchanged context is <see cref="ContextGaps"/>' job - sharing one mechanism
/// made expanding a member reveal context and collapsing all of them swallow the change.
/// </summary>
public static class DiffFolding
{
	/// <summary>
	/// IDE-style folds for types, methods, properties and events, in document lines. A diff
	/// document is not valid source of either side (they interleave, or are padded with
	/// filler), so the regions are found in one side's own text - by whichever provider
	/// serves that language - and mapped back here through the line map.
	/// </summary>
	public static List<FoldRange> Members(
		IReadOnlyList<Semantics.MemberFoldRegion> regions, IReadOnlyList<int> sideToDocLine)
	{
		var ranges = new List<FoldRange>();
		if (sideToDocLine.Count == 0)
			return ranges;
		foreach (var region in regions)
		{
			if (region.StartLine > sideToDocLine.Count || region.EndLine > sideToDocLine.Count)
				continue;
			int docStart = sideToDocLine[region.StartLine - 1];
			int docEnd = sideToDocLine[region.EndLine - 1];
			if (docEnd <= docStart)
				continue;
			ranges.Add(new FoldRange(docStart, docEnd, " ... ", DefaultClosed: false, FromHeaderEnd: true,
				HeaderEndLine: sideToDocLine[region.HeaderEndLine - 1]));
		}
		return ranges;
	}
}
