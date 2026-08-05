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
