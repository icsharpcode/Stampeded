using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;

using Stampeded.Core.Diff;

namespace Stampeded.Editor;

/// <summary>
/// Syntax colours for a unified-diff document, computed on each side's own text and then
/// transferred onto the rows that text ended up on.
///
/// A grammar is a state machine over consecutive lines: a Python <c>'''</c> string, a block
/// comment, a here-document all begin on one line and end on a later one. The unified
/// document is consecutive on neither side - it interleaves the two blobs and splices comment
/// rows between them - so a removed line that opens a span and the added line that closes it
/// switch the state on and off in places where neither file does, and everything below reads
/// in the wrong colour. Each side highlighted on its own sees only its own lines, in order.
/// </summary>
static class DiffSyntaxColors
{
	public static RichTextModel Build(SyntaxPainter painter, DiffDocumentModel model, TextDocument document)
	{
		var rich = new RichTextModel();
		AddSide(rich, painter, model, document, oldSide: false);
		if (model.Tags.Any(t => t.Kind == DiffLineKind.Removed))
			AddSide(rich, painter, model, document, oldSide: true);
		return rich;
	}

	/// <summary>One text painted onto itself, for a view that shows one revision whole - each
	/// pane of the side-by-side layout, where a side is not interleaved with anything.</summary>
	public static RichTextModel Whole(SyntaxPainter painter, TextDocument document)
	{
		var rich = new RichTextModel();
		foreach (var span in painter.Paint(document.Text))
		{
			if (span.Line > document.LineCount)
				break;
			var line = document.GetLineByNumber(span.Line);
			int length = Math.Min(span.Length, line.Length - span.Start);
			if (span.Start >= 0 && length > 0)
				rich.ApplyHighlighting(line.Offset + span.Start, length, span.Color);
		}
		return rich;
	}

	static void AddSide(RichTextModel rich, SyntaxPainter painter,
		DiffDocumentModel model, TextDocument document, bool oldSide)
	{
		var (text, sideToDoc) = model.GetSideText(oldSide);
		if (text.Length == 0)
			return;
		foreach (var span in painter.Paint(text))
		{
			int index = span.Line - 1;
			if (index >= sideToDoc.Count)
				break;
			int docLineNumber = sideToDoc[index];
			if (docLineNumber > document.LineCount || docLineNumber > model.Tags.Count)
				break;
			// The old side is painted onto the rows that are only its own: a context row shows
			// the same text on both sides and is already painted by the new one.
			if (oldSide && model.Tags[docLineNumber - 1].Kind != DiffLineKind.Removed)
				continue;
			var docLine = document.GetLineByNumber(docLineNumber);
			int length = Math.Min(span.Length, docLine.Length - span.Start);
			if (span.Start >= 0 && length > 0)
				rich.ApplyHighlighting(docLine.Offset + span.Start, length, span.Color);
		}
	}
}
