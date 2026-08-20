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
	public static RichTextModel Build(
		IHighlightingDefinition definition, DiffDocumentModel model, TextDocument document)
	{
		var rich = new RichTextModel();
		AddSide(rich, definition, model, document, oldSide: false);
		if (model.Tags.Any(t => t.Kind == DiffLineKind.Removed))
			AddSide(rich, definition, model, document, oldSide: true);
		return rich;
	}

	static void AddSide(RichTextModel rich, IHighlightingDefinition definition,
		DiffDocumentModel model, TextDocument document, bool oldSide)
	{
		var (text, sideToDoc) = model.GetSideText(oldSide);
		if (text.Length == 0)
			return;
		var sideDocument = new TextDocument(text);
		using var highlighter = new DocumentHighlighter(sideDocument, definition);
		highlighter.BeginHighlighting();
		for (int i = 0; i < sideToDoc.Count && i < sideDocument.LineCount; i++)
		{
			int docLineNumber = sideToDoc[i];
			if (docLineNumber > document.LineCount || docLineNumber > model.Tags.Count)
				break;
			// A context line is the same line on both sides; it is coloured once, from the
			// new side, so the old side's pass does not paint over it with the same thing.
			if (oldSide && model.Tags[docLineNumber - 1].Kind != DiffLineKind.Removed)
				continue;
			var sideLine = sideDocument.GetLineByNumber(i + 1);
			var docLine = document.GetLineByNumber(docLineNumber);
			// Nested sections - a keyword inside a span - arrive outermost first, and merging
			// is what the editor's own colorizer does with them: the inner colour overrides
			// only the attributes it sets.
			foreach (var section in highlighter.HighlightLine(i + 1).Sections)
			{
				int start = section.Offset - sideLine.Offset;
				int length = Math.Min(section.Length, docLine.Length - start);
				if (start >= 0 && length > 0)
					rich.ApplyHighlighting(docLine.Offset + start, length, section.Color);
			}
		}
		highlighter.EndHighlighting();
	}
}
