using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

using Stampeded.Core.Git;
using Stampeded.Themes;

namespace Stampeded.Diff;

/// <summary>
/// Per-line "sha author age" gutter, age-tinted (recent commits warmer). Hovering a row
/// shows the commit summary as a tooltip. Toggled with 'b' in the diff view.
/// </summary>
public sealed class BlameMargin : AbstractMargin
{
	const int MaxAuthorChars = 12;

	readonly Typeface typeface = new("Consolas, Menlo, Monospace");
	const double EmSize = 11;

	/// <summary>Invoked with the clicked row's blame info (drill into the commit).</summary>
	public Action<BlameLine>? CommitRequested { get; set; }

	IReadOnlyList<BlameLine?>? lines;
	long minTicks, maxTicks;
	double charWidth;

	public void SetLines(IReadOnlyList<BlameLine?> perDocLine)
	{
		lines = perDocLine;
		var stamps = perDocLine.Where(l => l is not null).Select(l => l!.AuthorTime.UtcTicks).ToList();
		minTicks = stamps.Count > 0 ? stamps.Min() : 0;
		maxTicks = stamps.Count > 0 ? stamps.Max() : 1;
		InvalidateMeasure();
		InvalidateVisual();
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		charWidth = Format("9", Brushes.Gray).Width;
		// "sha7 author.12 age4" + padding
		return new Size(charWidth * (7 + 1 + MaxAuthorChars + 1 + 4) + 8, 0);
	}

	FormattedText Format(string text, IBrush brush)
		=> new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, EmSize, brush);


	/// <summary>Whether a line carries a context gap's control, whose band runs across every
	/// gutter as well as the text - see <see cref="ContextGapChrome"/>.</summary>
	public Func<int, bool>? IsContextGapRow { get; set; }

	public override void Render(DrawingContext context)
	{
		var textView = TextView;
		ContextGapChrome.DrawRows(context, textView, Bounds.Width, IsContextGapRow);
		if (textView is null || !textView.VisualLinesValid || lines is null)
			return;
		bool dark = ThemeManager.Current.IsDarkTheme;
		var textBrush = dark ? Brushes.LightGray : Brushes.DimGray;

		string? previousSha = null;
		foreach (var visualLine in textView.VisualLines)
		{
			int lineNumber = visualLine.FirstDocumentLine.LineNumber;
			if (lineNumber > lines.Count)
				continue;
			var blame = lines[lineNumber - 1];
			double top = visualLine.VisualTop - textView.VerticalOffset;
			if (blame is null)
			{
				previousSha = null;
				continue;
			}

			// Age tint: newest commits get the strongest highlight.
			double age = maxTicks > minTicks
				? (blame.AuthorTime.UtcTicks - minTicks) / (double)(maxTicks - minTicks)
				: 0;
			byte alpha = (byte)(16 + age * 64);
			var tint = new SolidColorBrush(dark ? Color.FromArgb(alpha, 0x3A, 0x94, 0xFF) : Color.FromArgb(alpha, 0x00, 0x7A, 0xCC));
			context.FillRectangle(tint, new Rect(0, top, Bounds.Width, visualLine.Height));

			// Only the first row of a same-commit run gets text, like git GUIs do.
			if (blame.Sha != previousSha)
			{
				string author = blame.Author.Length > MaxAuthorChars ? blame.Author[..MaxAuthorChars] : blame.Author;
				string text = $"{blame.Sha[..7]} {author,-MaxAuthorChars} {FormatAge(blame.AuthorTime),4}";
				context.DrawText(Format(text, textBrush), new Point(4, top));
			}
			previousSha = blame.Sha;
		}
	}

	static string FormatAge(DateTimeOffset time)
	{
		var age = DateTimeOffset.UtcNow - time;
		return age.TotalDays switch {
			< 1 => $"{Math.Max(1, (int)age.TotalHours)}h",
			< 60 => $"{(int)age.TotalDays}d",
			< 700 => $"{(int)(age.TotalDays / 30)}mo",
			_ => $"{(int)(age.TotalDays / 365)}y",
		};
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
			return;
		var blame = HitTestLine(e.GetPosition(this).Y);
		if (blame is not null)
		{
			CommitRequested?.Invoke(blame);
			e.Handled = true;
		}
	}

	BlameLine? HitTestLine(double y)
	{
		var textView = TextView;
		if (textView is null || lines is null || !textView.VisualLinesValid)
			return null;
		double adjusted = y + textView.VerticalOffset;
		var visualLine = textView.VisualLines.FirstOrDefault(vl => vl.VisualTop <= adjusted && adjusted < vl.VisualTop + vl.Height);
		return visualLine is not null && visualLine.FirstDocumentLine.LineNumber <= lines.Count
			? lines[visualLine.FirstDocumentLine.LineNumber - 1]
			: null;
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		var textView = TextView;
		if (textView is null || lines is null || !textView.VisualLinesValid)
			return;
		double y = e.GetPosition(this).Y + textView.VerticalOffset;
		var visualLine = textView.VisualLines.FirstOrDefault(vl => vl.VisualTop <= y && y < vl.VisualTop + vl.Height);
		var blame = visualLine is not null && visualLine.FirstDocumentLine.LineNumber <= lines.Count
			? lines[visualLine.FirstDocumentLine.LineNumber - 1]
			: null;
		ToolTip.SetTip(this, blame is null
			? null
			: $"{blame.Sha[..9]} {blame.Author} {blame.AuthorTime.ToLocalTime():yyyy-MM-dd}\n{blame.Summary}");
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
