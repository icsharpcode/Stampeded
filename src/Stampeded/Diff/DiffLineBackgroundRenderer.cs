using Avalonia;
using Avalonia.Media;

using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

using Stampeded.Core.Diff;
using Stampeded.Themes;

namespace Stampeded.Diff;

/// <summary>
/// Paints full-width row backgrounds for added/removed lines plus stronger intra-line
/// word-diff tints, below the selection layer so syntax colors and selection compose on
/// top. Concept ported from Aehnlich's DiffLineBackgroundRenderer (MIT).
/// </summary>
public sealed class DiffLineBackgroundRenderer(Func<IReadOnlyList<DiffLineTag>?> tagsProvider) : IBackgroundRenderer
{
	// GitHub-style palette, light/dark variants.
	static readonly IBrush AddedLight = new SolidColorBrush(Color.Parse("#E6FFEC"));
	static readonly IBrush AddedWordLight = new SolidColorBrush(Color.Parse("#ABF2BC"));
	static readonly IBrush RemovedLight = new SolidColorBrush(Color.Parse("#FFEBE9"));
	static readonly IBrush RemovedWordLight = new SolidColorBrush(Color.Parse("#FFC0C0"));
	static readonly IBrush AddedDark = new SolidColorBrush(Color.Parse("#1C2E22"));
	static readonly IBrush AddedWordDark = new SolidColorBrush(Color.Parse("#2EA043"), 0.45);
	static readonly IBrush RemovedDark = new SolidColorBrush(Color.Parse("#382226"));
	static readonly IBrush RemovedWordDark = new SolidColorBrush(Color.Parse("#F85149"), 0.35);
	static readonly IBrush FillerBrush = new SolidColorBrush(Colors.Gray, 0.12);

	public KnownLayer Layer => KnownLayer.Background;

	public void Draw(TextView textView, DrawingContext drawingContext)
	{
		var tags = tagsProvider();
		if (tags is null || !textView.VisualLinesValid)
			return;

		bool dark = ThemeManager.Current.IsDarkTheme;
		var added = dark ? AddedDark : AddedLight;
		var removed = dark ? RemovedDark : RemovedLight;
		var addedWord = dark ? AddedWordDark : AddedWordLight;
		var removedWord = dark ? RemovedWordDark : RemovedWordLight;

		foreach (var visualLine in textView.VisualLines)
		{
			int lineNumber = visualLine.FirstDocumentLine.LineNumber;
			if (lineNumber > tags.Count)
				continue;
			var tag = tags[lineNumber - 1];
			var rowBrush = tag.Kind switch {
				DiffLineKind.Added => added,
				DiffLineKind.Removed => removed,
				DiffLineKind.Filler => FillerBrush,
				_ => null,
			};
			if (rowBrush is null)
				continue;

			double top = visualLine.VisualTop - textView.VerticalOffset;
			drawingContext.FillRectangle(rowBrush, new Rect(0, top, textView.Bounds.Width, visualLine.Height));

			if (tag.WordDiffs is { } spans)
			{
				var wordBrush = tag.Kind == DiffLineKind.Added ? addedWord : removedWord;
				int lineStart = visualLine.FirstDocumentLine.Offset;
				foreach (var span in spans)
				{
					var segment = new TextSegment { StartOffset = lineStart + span.Start, Length = span.Length };
					foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
						drawingContext.FillRectangle(wordBrush, rect);
				}
			}
		}
	}
}
