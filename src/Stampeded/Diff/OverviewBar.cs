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

		double height = Bounds.Height;
		double perLine = height / tags.Count;
		for (int i = 0; i < tags.Count; i++)
		{
			var brush = tags[i].Kind switch {
				DiffLineKind.Added => AddedTick,
				DiffLineKind.Removed => RemovedTick,
				_ => null,
			};
			if (brush is not null)
				context.FillRectangle(brush, new Rect(2, i * perLine, Bounds.Width - 4, Math.Max(2, perLine)));
		}

		// Viewport indicator from the text view's scroll state.
		var textView = editor.TextArea.TextView;
		double docHeight = Math.Max(1, textView.DocumentHeight);
		double top = textView.VerticalOffset / docHeight * height;
		double vpHeight = textView.Bounds.Height / docHeight * height;
		context.FillRectangle(ViewportBrush, new Rect(0, top, Bounds.Width, Math.Max(8, vpHeight)));
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
		int line = Math.Clamp((int)(y / Bounds.Height * tags.Count) + 1, 1, tags.Count);
		editor.ScrollToLine(line);
	}
}
