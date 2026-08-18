using Avalonia;
using Avalonia.Media;

using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

using Stampeded.Core.Diff;

namespace Stampeded.Diff;

/// <summary>
/// Thin per-line coverage strip: green for executed lines, red for executable-but-unhit
/// ones (head-side line numbers; lines absent from the coverage report draw nothing).
/// </summary>
public sealed class CoverageMargin : AbstractMargin
{
	static readonly IBrush Covered = new SolidColorBrush(Color.Parse("#2EA043"), 0.8);
	static readonly IBrush Uncovered = new SolidColorBrush(Color.Parse("#F85149"), 0.9);

	public IReadOnlyList<DiffLineTag>? Tags { get; set; }
	public IReadOnlyDictionary<int, int>? HitsByNewLine { get; set; }

	protected override Size MeasureOverride(Size availableSize) => new(5, 0);


	/// <summary>Whether a line carries a context gap's control, whose band runs across every
	/// gutter as well as the text - see <see cref="ContextGapChrome"/>.</summary>
	public Func<int, bool>? IsContextGapRow { get; set; }

	public override void Render(DrawingContext context)
	{
		var textView = TextView;
		ContextGapChrome.DrawRows(context, textView, Bounds.Width, IsContextGapRow);
		if (textView is null || !textView.VisualLinesValid || Tags is null || HitsByNewLine is null)
			return;
		foreach (var visualLine in textView.VisualLines)
		{
			int lineNumber = visualLine.FirstDocumentLine.LineNumber;
			if (lineNumber > Tags.Count)
				continue;
			var tag = Tags[lineNumber - 1];
			if (tag.NewLine == 0 || !HitsByNewLine.TryGetValue(tag.NewLine, out int hits))
				continue;
			double top = visualLine.VisualTop - textView.VerticalOffset;
			context.FillRectangle(hits > 0 ? Covered : Uncovered, new Rect(1, top, 3, visualLine.Height));
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
