namespace Stampeded.Core.Diff;

/// <summary>
/// A fold expressed in 1-based document lines, before it is turned into offsets against a
/// particular editor's document. The side-by-side view installs the same ranges in two
/// editors whose line lengths differ, so ranges and offsets have to stay separate.
/// </summary>
/// <param name="FromHeaderEnd">Fold from the end of the first line instead of its start,
/// so a member's signature stays visible while its body collapses.</param>
public sealed record FoldRange(int StartLine, int EndLine, string Name, bool DefaultClosed, bool FromHeaderEnd);

/// <summary>
/// Structural folding for the diff views: the code's own regions, and nothing about the
/// diff. Hiding unchanged context is <see cref="ContextGaps"/>' job - sharing one mechanism
/// made expanding a member reveal context and collapsing all of them swallow the change.
/// </summary>
public static class DiffFolding
{
	/// <summary>
	/// IDE-style folds for types, methods, properties and events. A diff document is not
	/// valid C# (either side is interleaved with the other, or padded with filler), so the
	/// side text is reconstructed from the line map, parsed, and the regions mapped back.
	/// </summary>
	public static List<FoldRange> Members(string sideText, IReadOnlyList<int> sideToDocLine)
	{
		var ranges = new List<FoldRange>();
		if (sideToDocLine.Count == 0)
			return ranges;
		foreach (var region in Roslyn.MemberFolding.Compute(sideText))
		{
			if (region.StartLine > sideToDocLine.Count || region.EndLine > sideToDocLine.Count)
				continue;
			int docStart = sideToDocLine[region.StartLine - 1];
			int docEnd = sideToDocLine[region.EndLine - 1];
			if (docEnd <= docStart)
				continue;
			ranges.Add(new FoldRange(docStart, docEnd, " ... ", DefaultClosed: false, FromHeaderEnd: true));
		}
		return ranges;
	}
}
