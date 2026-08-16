using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

using AvaloniaEdit;

using Stampeded.Core.Diff;

namespace Stampeded.Diff;

/// <summary>
/// The editor's vertical scrollbar, with the whole file's changes drawn on it: one tick per
/// changed line, and the viewport as the thumb over them. The editor's own scrollbar is
/// hidden in favour of this - two strips side by side, one showing where the change is and
/// one showing where you are, ask the reader which of them to drag.
///
/// Deliberately a tiny custom control instead of a scrollbar-template port: the arithmetic
/// is all there is to it, and the ticks have to be measured through the height tree anyway.
/// </summary>
public sealed class OverviewBar : Control
{
	public OverviewBar()
	{
		// Click-to-jump surface, not text.
		Cursor = new Cursor(StandardCursorType.Arrow);
	}

	static readonly IBrush AddedTick = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush RemovedTick = new SolidColorBrush(Color.Parse("#F85149"));
	static readonly IBrush CommentTick = new SolidColorBrush(Color.Parse("#D29922"));
	static readonly IBrush TrackBrush = new SolidColorBrush(Colors.Gray, 0.10);
	static readonly IBrush ThumbBrush = new SolidColorBrush(Colors.Gray, 0.35);
	static readonly IBrush ThumbHoverBrush = new SolidColorBrush(Colors.Gray, 0.55);
	static readonly IPen ThumbPen = new Pen(new SolidColorBrush(Colors.Gray, 0.7));

	TextEditor? editor;
	IReadOnlyList<DiffLineTag>? tags;
	bool hovering;
	double grabOffset = double.NaN;

	public void Attach(TextEditor editor, IReadOnlyList<DiffLineTag> tags)
	{
		this.editor = editor;
		this.tags = tags;
		editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => InvalidateVisual();
		InvalidateVisual();
	}

	public override void Render(DrawingContext context)
	{
		base.Render(context);
		// The whole strip, so a press anywhere on it is this control's to answer.
		context.FillRectangle(TrackBrush, new Rect(0, 0, Bounds.Width, Bounds.Height));
		if (tags is null || tags.Count == 0 || editor is null)
			return;

		// Map through the height tree, not line indices: comment-thread boxes make one
		// document line hundreds of pixels tall and collapsed folds do the inverse, so
		// linear per-line mapping drifts from where the scrollbar actually goes.
		var textView = editor.TextArea.TextView;
		double height = Bounds.Height;
		double docHeight = Math.Max(1, textView.DocumentHeight);
		int lineCount = Math.Min(tags.Count, editor.Document.LineCount);
		for (int i = 0; i < lineCount; i++)
		{
			var brush = tags[i].Kind switch {
				DiffLineKind.Added => AddedTick,
				DiffLineKind.Removed => RemovedTick,
				DiffLineKind.Comment => CommentTick,
				_ => null,
			};
			if (brush is null)
				continue;
			double top = textView.GetVisualTopByDocumentLine(i + 1) / docHeight * height;
			double bottom = (i + 2 <= editor.Document.LineCount
				? textView.GetVisualTopByDocumentLine(i + 2)
				: docHeight) / docHeight * height;
			context.FillRectangle(brush, new Rect(2, top, Bounds.Width - 4, Math.Max(2, bottom - top)));
		}

		// The thumb over the ticks rather than beside them, and translucent, so where the
		// reader is and what is left to read are one picture.
		var thumb = Thumb();
		context.FillRectangle(hovering || IsDragging ? ThumbHoverBrush : ThumbBrush, thumb, 2);
		context.DrawRectangle(null, ThumbPen, thumb, 2);
	}

	bool IsDragging => !double.IsNaN(grabOffset);

	/// <summary>Where the viewport sits on the strip, with a floor so a long file still has
	/// something to grab.</summary>
	Rect Thumb()
	{
		if (editor is null)
			return default;
		var textView = editor.TextArea.TextView;
		double docHeight = Math.Max(1, textView.DocumentHeight);
		double top = textView.VerticalOffset / docHeight * Bounds.Height;
		double height = Math.Max(12, textView.Bounds.Height / docHeight * Bounds.Height);
		return new Rect(0, Math.Min(top, Math.Max(0, Bounds.Height - height)), Bounds.Width, height);
	}

	protected override void OnPointerEntered(PointerEventArgs e)
	{
		base.OnPointerEntered(e);
		hovering = true;
		InvalidateVisual();
	}

	protected override void OnPointerExited(PointerEventArgs e)
	{
		base.OnPointerExited(e);
		hovering = false;
		InvalidateVisual();
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			return;
		double y = e.GetPosition(this).Y;
		var thumb = Thumb();
		// On the thumb, the reader is dragging what they are already looking at, so it keeps
		// the grip they took it by. Anywhere else means "take me there", and the drag
		// continues from the middle of the viewport that lands under the pointer.
		if (thumb.Contains(new Point(thumb.X, y)))
		{
			grabOffset = y - thumb.Y;
		}
		else
		{
			grabOffset = thumb.Height / 2;
			ScrollTo(y - grabOffset);
		}
		e.Pointer.Capture(this);
		e.Handled = true;
		InvalidateVisual();
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (IsDragging && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			ScrollTo(e.GetPosition(this).Y - grabOffset);
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		grabOffset = double.NaN;
		e.Pointer.Capture(null);
		InvalidateVisual();
	}

	/// <summary>The wheel over a scrollbar scrolls what it belongs to.</summary>
	protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
	{
		base.OnPointerWheelChanged(e);
		if (editor is null)
			return;
		double step = editor.TextArea.TextView.DefaultLineHeight * 3;
		ScrollBy(offset => offset - e.Delta.Y * step);
		e.Handled = true;
	}

	/// <summary>Puts the top of the viewport where <paramref name="top"/> is on the strip.</summary>
	void ScrollTo(double top)
	{
		if (editor is null || Bounds.Height <= 0)
			return;
		double docHeight = Math.Max(1, editor.TextArea.TextView.DocumentHeight);
		ScrollBy(offset => top / Bounds.Height * docHeight);
	}

	/// <summary>
	/// Moves the view, through the editor's own scroll viewer: TextEditor.ScrollToVerticalOffset
	/// is an empty method in AvaloniaEdit 12, so a control that asks it to scroll asks nothing.
	/// </summary>
	void ScrollBy(Func<double, double> target)
	{
		if (editor?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll)
			return;
		double max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
		scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(target(scroll.Offset.Y), 0, max));
		InvalidateVisual();
	}
}
