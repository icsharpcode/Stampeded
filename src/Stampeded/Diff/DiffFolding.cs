using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;

using Stampeded.Core.Diff;

namespace Stampeded.Diff;

/// <summary>Turns document-line fold ranges into offsets against one editor's document.</summary>
public static class FoldInstaller
{
	public static List<NewFolding> ToFoldings(TextDocument document, IEnumerable<FoldRange> ranges)
	{
		var foldings = new List<NewFolding>();
		foreach (var range in ranges)
		{
			if (range.StartLine < 1 || range.EndLine > document.LineCount || range.EndLine < range.StartLine)
				continue;
			var start = document.GetLineByNumber(range.StartLine);
			var end = document.GetLineByNumber(range.EndLine);
			int startOffset = range.FromHeaderEnd ? start.EndOffset : start.Offset;
			if (end.EndOffset <= startOffset)
				continue;
			foldings.Add(new NewFolding(startOffset, end.EndOffset) {
				Name = range.Name,
				DefaultClosed = range.DefaultClosed,
			});
		}
		return foldings.OrderBy(f => f.StartOffset).ToList();
	}
}

/// <summary>
/// A fold expressed in 1-based document lines, before it is turned into offsets against a
/// particular editor's document. The side-by-side view installs the same ranges in two
/// editors whose line lengths differ, so ranges and offsets have to stay separate.
/// </summary>
/// <param name="FromHeaderEnd">Fold from the end of the first line instead of its start,
/// so a member's signature stays visible while its body collapses.</param>
public sealed record FoldRange(int StartLine, int EndLine, string Name, bool DefaultClosed, bool FromHeaderEnd);

/// <summary>The folding policy shared by the unified and side-by-side diff views.</summary>
public static class DiffFolding
{
	/// <summary>Lines of unchanged context left visible on each side of a hunk.</summary>
	public const int Context = 3;

	/// <summary>
	/// Collapsible runs of unchanged lines. Nothing folds when the document has no
	/// changes at all, which is how a plain source view stays fully expanded.
	/// </summary>
	public static List<FoldRange> UnchangedRuns(IReadOnlyList<DiffLineTag> tags, bool hasChanges)
	{
		var ranges = new List<FoldRange>();
		if (!hasChanges)
			return ranges;
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
				Add(ranges, tags.Count, runStart, i - 1);
				runStart = -1;
			}
		}
		return ranges;
	}

	static void Add(List<FoldRange> ranges, int tagCount, int firstTag, int lastTag)
	{
		// Keep Context lines visible on each side; at the document edges the whole run may
		// fold except the context adjoining the hunk.
		int foldFirst = firstTag == 0 ? firstTag : firstTag + Context;
		int foldLast = lastTag == tagCount - 1 ? lastTag : lastTag - Context;
		int hidden = foldLast - foldFirst + 1;
		if (hidden < 2)
			return;
		ranges.Add(new FoldRange(foldFirst + 1, foldLast + 1,
			$"... {hidden} unchanged lines", DefaultClosed: true, FromHeaderEnd: false));
	}

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
		foreach (var region in Core.Roslyn.MemberFolding.Compute(sideText))
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
