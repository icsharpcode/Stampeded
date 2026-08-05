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

		var numberBrush = ThemeManager.Current.IsDarkTheme ? Brushes.DimGray : Brushes.Gray;
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

			// With one column shown it occupies the first slot, so a side-by-side pane
			// gutters only its own blob's numbers.
			if (ShowsOld && tag.OldLine > 0)
			{
				var ft = Format(tag.OldLine.ToString(CultureInfo.InvariantCulture), numberBrush);
				context.DrawText(ft, new Point(col1Right - ft.Width, top));
			}
			if (ShowsNew && tag.NewLine > 0)
			{
				var ft = Format(tag.NewLine.ToString(CultureInfo.InvariantCulture), numberBrush);
				double right = Columns == DiffLineNumberColumns.Both ? col2Right : col1Right;
				context.DrawText(ft, new Point(right - ft.Width, top));
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
