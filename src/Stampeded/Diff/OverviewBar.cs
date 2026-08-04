using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using AvaloniaEdit;

using Stampeded.Core.Diff;

namespace Stampeded.Diff;

/// <summary>
/// Whole-file diff overview strip: one tick per changed line plus a viewport indicator;
/// click or drag scrolls the editor. Deliberately a tiny custom control instead of a
/// scrollbar-template port (the click math is all there is to it).
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
	static readonly IBrush ViewportBrush = new SolidColorBrush(Colors.Gray, 0.25);

	TextEditor? editor;
	IReadOnlyList<DiffLineTag>? tags;

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
		context.FillRectangle(Brushes.Transparent, Bounds.WithX(0).WithY(0)); // hit-test area
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

		double vpTop = textView.VerticalOffset / docHeight * height;
		double vpHeight = textView.Bounds.Height / docHeight * height;
		context.FillRectangle(ViewportBrush, new Rect(0, vpTop, Bounds.Width, Math.Max(8, vpHeight)));
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		ScrollTo(e.GetPosition(this).Y);
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			ScrollTo(e.GetPosition(this).Y);
	}

	void ScrollTo(double y)
	{
		if (tags is null || tags.Count == 0 || editor is null)
			return;
		var textView = editor.TextArea.TextView;
		double docHeight = Math.Max(1, textView.DocumentHeight);
		double target = y / Bounds.Height * docHeight - textView.Bounds.Height / 2;
		editor.ScrollToVerticalOffset(Math.Max(0, target));
	}
}
