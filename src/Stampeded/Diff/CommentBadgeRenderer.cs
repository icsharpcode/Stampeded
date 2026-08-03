using System.Globalization;

using Avalonia;
using Avalonia.Media;

using AvaloniaEdit.Rendering;

using Stampeded.Themes;

namespace Stampeded.Diff;

public sealed record CommentBadge(bool IsDraft, string Author, string Body);

/// <summary>
/// Attaches existing review comments visually to their lines: a soft tint over the
/// commented row and a compact badge drawn after the end of the line. AvaloniaEdit
/// cannot host controls between lines without corrupting the diff line map, so this is
/// drawn, not inserted; the full bodies show on hover (hit rectangles are exposed).
/// </summary>
public sealed class CommentBadgeRenderer(
	Func<IReadOnlyDictionary<int, IReadOnlyList<CommentBadge>>?> badgesProvider,
	Func<(FontFamily Family, double Size)> fontProvider) : IBackgroundRenderer
{
	static readonly IBrush TintLight = new SolidColorBrush(Color.Parse("#FFF8C5"), 0.45);
	static readonly IBrush TintDark = new SolidColorBrush(Color.Parse("#4D4321"), 0.35);
	static readonly IBrush DraftBrush = new SolidColorBrush(Color.Parse("#D29922"));
	static readonly IBrush PostedLight = new SolidColorBrush(Color.Parse("#57606A"));
	static readonly IBrush PostedDark = new SolidColorBrush(Color.Parse("#8B949E"));

	readonly List<(Rect Rect, string FullText)> hitRects = [];

	/// <summary>Badge rectangles of the last draw, in text-view viewport coordinates,
	/// with the full comment text for hover.</summary>
	public IReadOnlyList<(Rect Rect, string FullText)> HitRects => hitRects;

	public KnownLayer Layer => KnownLayer.Text;

	public void Draw(TextView textView, DrawingContext drawingContext)
	{
		hitRects.Clear();
		var badges = badgesProvider();
		if (badges is null || badges.Count == 0 || !textView.VisualLinesValid)
			return;

		bool dark = ThemeManager.Current.IsDarkTheme;
		var tint = dark ? TintDark : TintLight;
		var posted = dark ? PostedDark : PostedLight;
		var (family, size) = fontProvider();
		var typeface = new Typeface(family);

		foreach (var visualLine in textView.VisualLines)
		{
			int lineNumber = visualLine.FirstDocumentLine.LineNumber;
			if (!badges.TryGetValue(lineNumber, out var lineBadges) || lineBadges.Count == 0)
				continue;

			double top = visualLine.VisualTop - textView.VerticalOffset;
			drawingContext.FillRectangle(tint, new Rect(0, top, textView.Bounds.Width, visualLine.Height));

			double textEndX = visualLine.GetTextLineVisualXPosition(visualLine.TextLines[^1], visualLine.VisualLength)
				- textView.HorizontalOffset;
			double x = Math.Max(textEndX + 24, 60);
			bool anyDraft = lineBadges.Any(b => b.IsDraft);
			string label = string.Join("  |  ", lineBadges.Select(Summarize));
			var formatted = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
				typeface, size - 1, anyDraft ? DraftBrush : posted);
			formatted.MaxTextWidth = Math.Max(80, textView.Bounds.Width - x - 8);
			double y = top + (visualLine.Height - formatted.Height) / 2;
			drawingContext.DrawText(formatted, new Point(x, y));
			hitRects.Add((new Rect(x, top, formatted.Width, visualLine.Height),
				string.Join("\n\n", lineBadges.Select(b => $"{(b.IsDraft ? "[draft] " : "")}{b.Author}: {b.Body}"))));
		}
	}

	static string Summarize(CommentBadge badge)
	{
		string firstLine = badge.Body.ReplaceLineEndings("\n").Split('\n')[0];
		if (firstLine.Length > 70)
			firstLine = firstLine[..70] + "...";
		return $"{(badge.IsDraft ? "[draft] " : "")}{badge.Author}: {firstLine}";
	}
}
