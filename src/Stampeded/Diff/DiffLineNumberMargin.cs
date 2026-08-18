using System.Globalization;

using Avalonia;
using Avalonia.Media;

using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

using Stampeded.Core.Diff;
using Stampeded.Themes;

namespace Stampeded.Diff;

/// <summary>Which blob's line numbers a gutter shows.</summary>
public enum DiffLineNumberColumns
{
	/// <summary>Both columns, for the unified diff where one document carries both sides.</summary>
	Both,
	/// <summary>The old blob only, for the left pane of a side-by-side view.</summary>
	Old,
	/// <summary>The new blob only, for the right pane of a side-by-side view.</summary>
	New,
}

/// <summary>
/// Line-number gutter for a diff: old-blob number, new-blob number, and a 3px change
/// strip at the right edge. A line missing on the shown side leaves the column blank (the
/// nullable-line-number idea from Aehnlich's CustomLineNumberMargin).
/// </summary>
public sealed class DiffLineNumberMargin : AbstractMargin
{
	const double ColumnGap = 8;
	const double StripWidth = 3;
	const double RightPadding = 4;

	static readonly IBrush AddedStrip = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush RemovedStrip = new SolidColorBrush(Color.Parse("#F85149"));

	readonly Typeface typeface = new("Consolas, Menlo, Monospace");
	const double EmSize = 12;

	public IReadOnlyList<DiffLineTag>? Tags { get; set; }

	public DiffLineNumberColumns Columns { get; set; } = DiffLineNumberColumns.Both;

	/// <summary>Whether a line carries a context gap's control, which is drawn over the text
	/// but reads as a band across the whole pane - so the gutter paints its share of the row.
	/// </summary>
	public Func<int, bool>? IsContextGapRow { get; set; }

	bool ShowsOld => Columns is DiffLineNumberColumns.Both or DiffLineNumberColumns.Old;
	bool ShowsNew => Columns is DiffLineNumberColumns.Both or DiffLineNumberColumns.New;
	int ColumnCount => Columns == DiffLineNumberColumns.Both ? 2 : 1;

	double digitWidth;
	int digits = 4;

	protected override Size MeasureOverride(Size availableSize)
	{
		digitWidth = Measure("9").Width;
		int maxLine = 1;
		if (Tags is { Count: > 0 })
		{
			foreach (var tag in Tags)
				maxLine = Math.Max(maxLine, Math.Max(tag.OldLine, tag.NewLine));
		}
		digits = Math.Max(3, maxLine.ToString(CultureInfo.InvariantCulture).Length);
		return new Size(ColumnCount * (digits * digitWidth + ColumnGap) + StripWidth + RightPadding, 0);
	}

	FormattedText Measure(string text) => Format(text, Brushes.Gray);

	FormattedText Format(string text, IBrush brush)
		=> new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, EmSize, brush);

	public override void Render(DrawingContext context)
	{
		var textView = TextView;
		var tags = Tags;
		if (textView is null || !textView.VisualLinesValid || tags is null)
			return;

		ContextGapChrome.DrawRows(context, textView, Bounds.Width, IsContextGapRow);

		var numberBrush = ThemeManager.Current.IsDarkTheme ? Brushes.DimGray : Brushes.Gray;
		double numberHeight = Measure("9").Height;
		double col1Right = digits * digitWidth + ColumnGap;
		double col2Right = col1Right + digits * digitWidth + ColumnGap;
		double stripX = Bounds.Width - StripWidth - RightPadding + StripWidth;

		foreach (var visualLine in textView.VisualLines)
		{
			int lineNumber = visualLine.FirstDocumentLine.LineNumber;
			if (lineNumber > tags.Count)
				continue;
			var tag = tags[lineNumber - 1];
			double top = visualLine.VisualTop - textView.VerticalOffset;
			// Centered in the row rather than sitting at its top: a row carrying a context
			// gap's control is taller than a line of text, and a number pinned to the top of
			// it reads as belonging to the row above.
			double textTop = top + (visualLine.Height - numberHeight) / 2;

			double newRight = Columns == DiffLineNumberColumns.Both ? col2Right : col1Right;
			// A row standing for a run of hidden lines has no one number to show, so each
			// column says "there is more here" instead. Drawn rather than written: the gutter's
			// mono font is not guaranteed to carry a vertical ellipsis.
			if (IsContextGapRow?.Invoke(lineNumber) == true)
			{
				double middle = top + visualLine.Height / 2;
				if (ShowsOld)
					DrawEllipsis(context, col1Right - digitWidth / 2, middle, numberBrush);
				if (ShowsNew)
					DrawEllipsis(context, newRight - digitWidth / 2, middle, numberBrush);
			}
			else
			{
				// With one column shown it occupies the first slot, so a side-by-side pane
				// gutters only its own blob's numbers.
				if (ShowsOld && tag.OldLine > 0)
				{
					var ft = Format(tag.OldLine.ToString(CultureInfo.InvariantCulture), numberBrush);
					context.DrawText(ft, new Point(col1Right - ft.Width, textTop));
				}
				if (ShowsNew && tag.NewLine > 0)
				{
					var ft = Format(tag.NewLine.ToString(CultureInfo.InvariantCulture), numberBrush);
					context.DrawText(ft, new Point(newRight - ft.Width, textTop));
				}
			}
			var strip = tag.Kind switch {
				DiffLineKind.Added => AddedStrip,
				DiffLineKind.Removed => RemovedStrip,
				_ => null,
			};
			if (strip is not null)
				context.FillRectangle(strip, new Rect(stripX - StripWidth, top, StripWidth, visualLine.Height));
		}
	}

	/// <summary>Three dots stacked where a line number would be.</summary>
	static void DrawEllipsis(DrawingContext context, double centerX, double centerY, IBrush brush)
	{
		const double Radius = 1.1;
		const double Spacing = 4.5;
		for (int i = -1; i <= 1; i++)
			context.DrawEllipse(brush, null, new Point(centerX, centerY + i * Spacing), Radius, Radius);
	}

	protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
	{
		if (oldTextView is not null)
			oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
		base.OnTextViewChanged(oldTextView, newTextView);
		if (newTextView is not null)
			newTextView.VisualLinesChanged += OnVisualLinesChanged;
		InvalidateVisual();
	}

	void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();
}
