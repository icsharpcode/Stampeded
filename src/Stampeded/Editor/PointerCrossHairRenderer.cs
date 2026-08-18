#if DEBUG
using System.Globalization;

using Avalonia;
using Avalonia.Media;

using AvaloniaEdit.Rendering;

namespace Stampeded.Editor;

/// <summary>
/// Where the app thinks the pointer is: a cross-hair across the viewport with a readout of the
/// viewport coordinates and the document position they resolve to.
///
/// Hover docs and the comment popup are placed at the pointer, so when one opens somewhere
/// unexpected - or does not open at all - the question is always whether the pointer position
/// the code saw is the one on screen. Reading a coordinate out of a log answers that slowly;
/// this answers it by looking.
///
/// A debug build only, and inert there until switched on from the View menu. The switch is
/// global, so every open view draws it at once - the two diff layouts and their panes are
/// exactly what tends to be compared.
/// </summary>
sealed class PointerCrossHairRenderer : IBackgroundRenderer
{
	public static bool IsEnabled { get; set; }

	static readonly IPen LinePen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0x40, 0x40)), 1).ToImmutable();
	static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x00)).ToImmutable();
	const double LabelFontSize = 11;
	const double LabelOffset = 8;
	const double LabelPadding = 3;

	readonly TextView textView;
	// Viewport-relative, and null while the pointer is outside the view.
	Point? pointerPosition;
	// True while a cross-hair is on screen, so the handlers still repaint once after it is
	// switched off or the pointer leaves - otherwise the last frame stays painted.
	bool painted;

	public PointerCrossHairRenderer(TextView textView)
	{
		this.textView = textView;
		textView.BackgroundRenderers.Add(this);
		textView.PointerMoved += (_, e) => UpdatePointer(e.GetPosition(textView));
		textView.PointerExited += (_, _) => UpdatePointer(null);
	}

	public KnownLayer Layer => KnownLayer.Caret;

	void UpdatePointer(Point? point)
	{
		pointerPosition = point;
		if ((IsEnabled && point is not null) || painted)
		{
			// Every layer, because TextView.InvalidateLayer only invalidates the text view's
			// own measure and never re-renders the per-layer children - the same workaround
			// CaretHighlightAdorner needs.
			foreach (var layer in textView.Layers)
				layer.InvalidateVisual();
		}
	}

	public void Draw(TextView view, DrawingContext context)
	{
		if (!IsEnabled || pointerPosition is not { } point)
		{
			painted = false;
			return;
		}
		painted = true;
		var bounds = view.Bounds;
		// On the pixel center, so a one-pixel line lands on one device pixel rather than
		// straddling two - which matters when the whole point is comparing positions.
		double x = Math.Floor(point.X) + 0.5;
		double y = Math.Floor(point.Y) + 0.5;
		context.DrawLine(LinePen, new Point(x, 0), new Point(x, bounds.Height));
		context.DrawLine(LinePen, new Point(0, y), new Point(bounds.Width, y));

		// The pointer is viewport-relative; resolving it to a document position adds the scroll
		// offset, which is the same mapping the hover uses.
		var documentPosition = view.GetPosition(point + view.ScrollOffset);
		string label = documentPosition is { } position
			? $"({point.X:f0}, {point.Y:f0})  Ln {position.Line}, Col {position.Column}"
			: $"({point.X:f0}, {point.Y:f0})  no text position";
		var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
			Typeface.Default, LabelFontSize, Brushes.White);

		// Below-right of the crossing, flipping near an edge so the readout stays on screen.
		double labelX = point.X + LabelOffset;
		double labelY = point.Y + LabelOffset;
		if (labelX + text.Width + (2 * LabelPadding) > bounds.Width)
			labelX = point.X - LabelOffset - text.Width - (2 * LabelPadding);
		if (labelY + text.Height + (2 * LabelPadding) > bounds.Height)
			labelY = point.Y - LabelOffset - text.Height - (2 * LabelPadding);
		context.DrawRectangle(LabelBackground, null,
			new Rect(labelX, labelY, text.Width + (2 * LabelPadding), text.Height + (2 * LabelPadding)), 3, 3);
		context.DrawText(text, new Point(labelX + LabelPadding, labelY + LabelPadding));
	}
}
#endif
