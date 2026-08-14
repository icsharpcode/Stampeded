using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

using Stampeded.Core.Diff;

namespace Stampeded.Diff;

/// <summary>
/// Hides a diff's unchanged runs and hands them back a step at a time, through a control
/// drawn where the first hidden line would be.
///
/// The hiding is done with the editor's collapsed sections directly rather than through a
/// FoldingManager, which leaves folding to the code's own structure - types, members,
/// #regions. Sharing one manager made the two fight: expanding a method to read it also
/// revealed unrelated context, collapsing everything swallowed the change itself, and the
/// two kinds of region do not always nest.
///
/// One view drives several editors, and has to for the side-by-side panes: they are kept in
/// step by copying the scroll offset, which is exact only while both render the same rows.
/// </summary>
public sealed class ContextGapView
{
	readonly TextEditor[] editors;
	readonly ContextGapElementGenerator[] generators;
	readonly List<CollapsedLineSection>[] collapsed;
	readonly TextDocument?[] collapsedIn;
	List<ContextGap> gaps = [];

	public ContextGapView(params TextEditor[] editors)
	{
		this.editors = editors;
		generators = new ContextGapElementGenerator[editors.Length];
		collapsed = new List<CollapsedLineSection>[editors.Length];
		collapsedIn = new TextDocument?[editors.Length];
		for (int i = 0; i < editors.Length; i++)
		{
			collapsed[i] = [];
			generators[i] = new ContextGapElementGenerator { BarFactory = BuildBar };
			editors[i].TextArea.TextView.ElementGenerators.Add(generators[i]);
		}
	}

	public IReadOnlyList<ContextGap> Gaps => gaps;

	/// <summary>Raised whenever what is hidden changes, so anything derived from it - the
	/// structural folds, which must not offer to collapse code that is not shown - can
	/// follow.</summary>
	public event Action? Changed;

	/// <summary>Whether a line is currently hidden as context.</summary>
	public bool Hides(int line) => gaps.Any(g => g.Contains(line));

	/// <summary>The gaps of a freshly loaded document, all closed.</summary>
	/// <param name="memberStarts">Document lines where a member's declaration begins; a gap
	/// ending just under one keeps it visible.</param>
	public void Install(IReadOnlyList<DiffLineTag> tags, bool hasChanges, IReadOnlyList<int>? memberStarts = null)
	{
		gaps = ContextGaps.Compute(tags, hasChanges, memberStarts);
		Apply();
	}

	/// <summary>Reinstates gaps carried over from a document that was rebuilt underneath.</summary>
	public void Restore(IEnumerable<ContextGap> carried)
	{
		gaps = [.. carried];
		Apply();
	}

	public void Clear()
	{
		gaps = [];
		Apply();
	}

	/// <summary>
	/// Opens the gap hiding a line, so that navigating to it lands somewhere visible. A line
	/// that is not hidden needs nothing, which is what the false says.
	/// </summary>
	public bool Reveal(int docLine)
	{
		int index = gaps.FindIndex(g => g.Contains(docLine));
		if (index < 0)
			return false;
		gaps.RemoveAt(index);
		Apply();
		return true;
	}

	void Apply()
	{
		for (int i = 0; i < editors.Length; i++)
		{
			var editor = editors[i];
			var document = editor.Document;
			// Sections belong to the document they were made in; after a rebuild the old ones
			// are already gone with it, and uncollapsing them would throw.
			if (ReferenceEquals(collapsedIn[i], document))
			{
				foreach (var section in collapsed[i])
					section.Uncollapse();
			}
			collapsed[i].Clear();
			collapsedIn[i] = document;
			var view = editor.TextArea.TextView;
			var barLines = new Dictionary<int, ContextGap>();
			foreach (var gap in gaps)
			{
				if (gap.FirstLine < 1 || gap.LastLine > document.LineCount)
					continue;
				barLines[gap.FirstLine] = gap;
				// The bar takes the place of the first hidden line's text, so only the rest
				// of the run is collapsed - the same shape the fold placeholder had.
				if (gap.LastLine > gap.FirstLine)
				{
					collapsed[i].Add(view.CollapseLines(
						document.GetLineByNumber(gap.FirstLine + 1),
						document.GetLineByNumber(gap.LastLine)));
				}
			}
			generators[i].BarLines = barLines;
			view.Redraw();
		}
		Changed?.Invoke();
	}

	void Replace(ContextGap gap, ContextGap? replacement)
	{
		int index = gaps.IndexOf(gap);
		if (index < 0)
			return;
		// Revealing inserts rows at the gap, sliding everything after it down the screen -
		// which is the code the reader is looking at. The line that followed the gap is
		// pinned to the row it already occupied, so the context appears above what is being
		// read instead of pushing it away.
		var anchors = editors.Select(e => CaptureBelow(e, gap)).ToArray();
		if (replacement is null)
			gaps.RemoveAt(index);
		else
			gaps[index] = replacement;
		Apply();
		// Queued behind the layout pass the change triggers: until it runs, the rows the
		// anchor is measured against are the old ones.
		Avalonia.Threading.Dispatcher.UIThread.Post(
			() => {
				for (int i = 0; i < editors.Length; i++)
					RestoreBelow(editors[i], anchors[i]);
			},
			Avalonia.Threading.DispatcherPriority.Loaded);
	}

	/// <summary>The first visible line after a gap, and where it sits on screen.</summary>
	static (int Line, double Delta)? CaptureBelow(TextEditor editor, ContextGap gap)
	{
		var view = editor.TextArea.TextView;
		if (!view.VisualLinesValid)
			return null;
		foreach (var visual in view.VisualLines)
		{
			// The visual line carrying the control spans the whole gap, so the next one is
			// the content that follows it.
			if (visual.FirstDocumentLine.LineNumber > gap.LastLine)
				return (visual.FirstDocumentLine.LineNumber, visual.VisualTop - view.VerticalOffset);
		}
		return null;
	}

	static void RestoreBelow(TextEditor editor, (int Line, double Delta)? anchor)
	{
		if (anchor is not { } pinned || pinned.Line > editor.Document.LineCount)
			return;
		// Through the editor's ScrollViewer: TextEditor.ScrollToVerticalOffset is an empty
		// method in AvaloniaEdit 12, so anything asking it to scroll is asking nothing.
		if (editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll)
			return;
		double target = editor.TextArea.TextView.GetVisualTopByDocumentLine(pinned.Line) - pinned.Delta;
		if (Math.Abs(target - scroll.Offset.Y) > 0.5)
			scroll.Offset = new Avalonia.Vector(scroll.Offset.X, Math.Max(0, target));
	}

	Control BuildBar(ContextGap gap)
	{
		var row = new StackPanel {
			Orientation = Avalonia.Layout.Orientation.Horizontal,
			Spacing = 4,
			VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
		};
		if (gap.HiddenCount > ContextGaps.Step)
		{
			row.Children.Add(Step($"↓ {ContextGaps.Step}",
				$"Reveal the {ContextGaps.Step} lines below this point",
				() => Replace(gap, ContextGaps.RevealTop(gap, ContextGaps.Step))));
			row.Children.Add(Step($"↑ {ContextGaps.Step}",
				$"Reveal the {ContextGaps.Step} lines above the next change",
				() => Replace(gap, ContextGaps.RevealBottom(gap, ContextGaps.Step))));
		}
		row.Children.Add(Step($"↕ all {gap.HiddenCount}",
			"Reveal every hidden line here",
			() => Replace(gap, null)));
		row.Children.Add(new TextBlock {
			Text = $"{gap.HiddenCount} unchanged lines",
			FontSize = 11,
			Opacity = 0.6,
			Margin = new Avalonia.Thickness(6, 0, 0, 0),
			VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
		});
		return new Border {
			Background = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
			BorderThickness = new Avalonia.Thickness(0, 1),
			Padding = new Avalonia.Thickness(4, 0),
			Child = row,
		};

		static Button Step(string label, string tip, Action click)
		{
			var button = new Button {
				Content = label,
				FontSize = 10,
				Padding = new Avalonia.Thickness(5, 0),
				Cursor = new Cursor(StandardCursorType.Hand),
				[ToolTip.TipProperty] = tip,
			};
			button.Click += (_, _) => click();
			return button;
		}
	}
}

/// <summary>
/// Draws a gap's control in place of the line it hides behind. The element covers the whole
/// line, so the line's own text does not show through it.
/// </summary>
sealed class ContextGapElementGenerator : VisualLineElementGenerator
{
	public Func<ContextGap, Control>? BarFactory { get; set; }

	public IReadOnlyDictionary<int, ContextGap> BarLines { get; set; } =
		new Dictionary<int, ContextGap>();

	public override int GetFirstInterestedOffset(int startOffset)
	{
		if (BarFactory is null || BarLines.Count == 0)
			return -1;
		var line = CurrentContext.Document.GetLineByOffset(startOffset);
		return startOffset == line.Offset && BarLines.ContainsKey(line.LineNumber) ? startOffset : -1;
	}

	public override VisualLineElement? ConstructElement(int offset)
	{
		if (BarFactory is null)
			return null;
		var document = CurrentContext.Document;
		var line = document.GetLineByOffset(offset);
		if (!BarLines.TryGetValue(line.LineNumber, out var gap))
			return null;
		// The element has to span every line the gap hides, not just the one it is drawn on:
		// a visual line may cover several document lines only while an element accounts for
		// their text, and the collapsed lines that follow would otherwise each be asked to
		// start a visual line of their own - which a collapsed line cannot do.
		int lastLine = Math.Min(gap.LastLine, document.LineCount);
		int end = document.GetLineByNumber(lastLine).EndOffset;
		return end <= offset ? null : new InlineObjectElement(end - offset, BarFactory(gap));
	}
}
