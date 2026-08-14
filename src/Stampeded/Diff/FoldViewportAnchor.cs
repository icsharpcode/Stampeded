using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Avalonia.VisualTree;

using AvaloniaEdit;

namespace Stampeded.Diff;

/// <summary>
/// Keeps the reading position still when a fold opens or closes. Expanding inserts lines
/// at the fold, sliding everything after it down the document; the scroll offset is in
/// pixels, so whatever was on screen moves by the height of the revealed text. This pins
/// the topmost visible line to the row it already occupied and scrolls underneath it.
/// </summary>
public static class FoldViewportAnchor
{
	public static void Install(TextEditor editor)
	{
		// Fold margins live inside the TextArea, so a tunnelling handler sees the press
		// before the margin acts on it - and the capture has to happen before, because by
		// the time the fold has changed the old layout is gone.
		editor.TextArea.AddHandler(InputElement.PointerPressedEvent, (_, _) => {
			if (Capture(editor) is not { } anchor)
				return;
			// Queued behind the layout pass that the fold change triggers.
			Dispatcher.UIThread.Post(() => Restore(editor, anchor), DispatcherPriority.Loaded);
		}, RoutingStrategies.Tunnel, handledEventsToo: true);
	}

	/// <summary>Runs an action that changes folding and restores the reading position
	/// after it, for programmatic folding with no click to hang the capture on.</summary>
	public static void Preserving(TextEditor editor, Action action)
	{
		var anchor = Capture(editor);
		action();
		if (anchor is { } a)
			Restore(editor, a);
	}

	static (int Line, double Delta)? Capture(TextEditor editor)
	{
		var textView = editor.TextArea.TextView;
		if (!textView.VisualLinesValid || textView.VisualLines.Count == 0)
			return null;
		var first = textView.VisualLines[0];
		return (first.FirstDocumentLine.LineNumber, first.VisualTop - textView.VerticalOffset);
	}

	static void Restore(TextEditor editor, (int Line, double Delta) anchor)
	{
		if (anchor.Line > editor.Document.LineCount)
			return;
		// The editor's ScrollViewer, not TextEditor.ScrollToVerticalOffset: that method's
		// whole body in AvaloniaEdit 12 is a call to ApplyTemplate, so it scrolls nothing and
		// this anchor held nothing still for as long as it called it.
		if (editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll)
			return;
		double target = editor.TextArea.TextView.GetVisualTopByDocumentLine(anchor.Line) - anchor.Delta;
		if (Math.Abs(target - scroll.Offset.Y) > 0.5)
			scroll.Offset = new Avalonia.Vector(scroll.Offset.X, Math.Max(0, target));
	}
}
