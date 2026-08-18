using Avalonia.Input;

using AvaloniaEdit.Rendering;

namespace Stampeded.Editor;

/// <summary>
/// The pointer over a collapsed fold's placeholder, which is drawn text rather than a control:
/// nothing about it says "clickable" unless the view says so.
///
/// One implementation for both layouts. The unified view and a side-by-side pane show the same
/// rows with the same folds, and a reader moving between them should not find the placeholder
/// behaving differently in one.
/// </summary>
static class FoldCursor
{
	/// <summary>Sets the cursor for where the pointer now is, and answers whether it is over a
	/// placeholder - which the caller keeps, so the cursor is only assigned when it changes.
	/// </summary>
	public static bool Update(TextView view, PointerEventArgs e, bool wasOverFold)
	{
		bool overFold = false;
		var point = e.GetPosition(view) + view.ScrollOffset;
		if (view.GetVisualLineFromVisualTop(point.Y) is { } visualLine)
		{
			var textLine = visualLine.GetTextLineByVisualYPosition(point.Y);
			int column = visualLine.GetVisualColumn(textLine, point.X, allowVirtualSpace: false);
			var element = visualLine.Elements.FirstOrDefault(el =>
				el.VisualColumn <= column && column < el.VisualColumn + el.VisualLength);
			// By name, because the element type that stands for a collapsed section is internal
			// to the editor.
			overFold = element?.GetType().Name.Contains("Folding", StringComparison.Ordinal) == true;
		}
		if (overFold != wasOverFold)
			view.Cursor = new Cursor(overFold ? StandardCursorType.Hand : StandardCursorType.Ibeam);
		return overFold;
	}
}
