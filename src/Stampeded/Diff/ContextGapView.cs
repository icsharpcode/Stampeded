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
			var view = editors[i].TextArea.TextView;
			view.BackgroundRenderers.Add(new ContextGapBackgroundRenderer(HasBar));
			// Centering the controls is driven by layout, not by rendering: it measures their
			// arranged bounds, which do not exist yet the first time the row is painted. Run
			// from a render pass instead, a freshly opened diff kept its bars off center until
			// something unrelated happened to repaint it.
			view.LayoutUpdated += (_, _) => CenterBars(view);
		}
	}

	// Reveal-downwards, reveal-upwards and reveal-everything, in a 10x13 box.
	const string DownArrow = "M5,1.5 V11 M1.6,7.6 L5,11 L8.4,7.6";
	const string UpArrow = "M5,11.5 V2 M1.6,5.4 L5,2 L8.4,5.4";
	const string BothArrow = "M5,1.5 V11.5 M2,4.5 L5,1.5 L8,4.5 M2,8.5 L5,11.5 L8,8.5";

	public IReadOnlyList<ContextGap> Gaps => gaps;

	/// <summary>Raised whenever what is hidden changes, so anything derived from it - the
	/// structural folds, which must not offer to collapse code that is not shown - can
	/// follow.</summary>
	public event Action? Changed;

	/// <summary>Whether a line is currently hidden as context.</summary>
	public bool Hides(int line) => gaps.Any(g => g.Contains(line));

	/// <summary>Whether a line is the one a gap's control is drawn on. The gutters ask, so
	/// that the row they draw beside it carries the same background.</summary>
	public bool HasBar(int line) => gaps.Any(g => g.FirstLine == line);

	/// <summary>
	/// The structural folds that can be installed as things stand, with any that run into
	/// hidden context cut back to where it starts.
	///
	/// The two ways of hiding lines here are deliberately separate - folds are the code's own
	/// structure, gaps are the diff's - and they do not nest: a member whose tail is unchanged
	/// ends inside the gap hiding it. Collapsing such a fold would leave the editor with two
	/// collapsed sections that overlap without containing one another, which it cannot lay out
	/// ("Trying to build visual line from collapsed line") and which took the window down.
	///
	/// A fold that begins inside hidden context is dropped rather than cut: the gap's control
	/// stands for all of those lines at once, so the margin would draw a marker beside it
	/// offering to collapse code the reader cannot see. Both come back as the context does.
	/// </summary>
	public IReadOnlyList<FoldRange> ClipToVisible(IEnumerable<FoldRange> ranges)
	{
		var kept = new List<FoldRange>();
		foreach (var range in ranges)
		{
			if (Hides(range.StartLine))
				continue;
			// The first hidden line of the gap its end runs into; the gap's own control sits on
			// the line before it and stays reachable.
			int end = range.EndLine;
			if (gaps.FirstOrDefault(g => g.Contains(end)) is { } gap)
				end = gap.FirstLine - 1;
			if (end > range.StartLine)
				kept.Add(range with { EndLine = end });
		}
		return kept;
	}

	/// <summary>The gaps of a freshly loaded document, all closed.</summary>
	/// <param name="declarations">The code's structural ranges, in document lines; a run hiding
	/// the header of a declaration the change is inside is cut around it.</param>
	public void Install(IReadOnlyList<DiffLineTag> tags, bool hasChanges, IReadOnlyList<FoldRange>? declarations = null)
	{
		gaps = ContextGaps.Compute(tags, hasChanges, declarations);
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

	/// <summary>
	/// Puts each gap's control on the middle of the row it was laid out in. An inline object
	/// hangs from the text baseline, which leaves it high in a row taller than a line of text,
	/// and by how much follows from metrics the element generator cannot see - so the
	/// correction is measured from what was actually arranged. A render transform moves the
	/// control without changing that layout, so what it was measured from stays true.
	/// </summary>
	void CenterBars(TextView view)
	{
		if (!view.VisualLinesValid)
			return;
		foreach (var visualLine in view.VisualLines)
		{
			if (!HasBar(visualLine.FirstDocumentLine.LineNumber)
				|| visualLine.Elements.OfType<InlineObjectElement>().FirstOrDefault()?.Element is not { } control
				|| control.Bounds.Height <= 0)
			{
				continue;
			}
			// Measured on the buttons rather than on the control holding them: rounding inside
			// the control leaves them slightly off its center, and it is the buttons a reader
			// sees against the row.
			var content = (control as Border)?.Child is { Bounds.Height: > 0 } child ? child.Bounds : default;
			bool hasContent = content.Height > 0;
			double contentTop = control.Bounds.Y + (hasContent ? content.Y : 0);
			double contentHeight = hasContent ? content.Height : control.Bounds.Height;
			double rowTop = visualLine.VisualTop - view.VerticalOffset;
			double shift = (visualLine.Height - contentHeight) / 2 - (contentTop - rowTop);
			double applied = control.RenderTransform is TranslateTransform current ? current.Y : 0;
			if (Math.Abs(applied - shift) >= 0.5)
				control.RenderTransform = new TranslateTransform(0, shift);
		}
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

	/// <param name="slackPerEdge">Half of the empty height the row grows by around the control,
	/// which its margin gives back.</param>
	Control BuildBar(ContextGap gap, double slackPerEdge)
	{
		var row = new StackPanel {
			Orientation = Avalonia.Layout.Orientation.Horizontal,
			Spacing = 4,
			VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
		};
		if (gap.HiddenCount > ContextGaps.Step)
		{
			// Stacked rather than side by side, in the order of what they reveal: the top one
			// opens the run downwards from the code above, the bottom one upwards from the code
			// below. Next to each other the two arrows were a puzzle to tell apart.
			var steps = new StackPanel {
				Orientation = Avalonia.Layout.Orientation.Vertical,
				Spacing = 0,
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
			};
			steps.Children.Add(Step(DownArrow, $"{ContextGaps.Step}",
				$"Reveal the {ContextGaps.Step} lines below this point",
				() => Replace(gap, ContextGaps.RevealTop(gap, ContextGaps.Step))));
			steps.Children.Add(Step(UpArrow, $"{ContextGaps.Step}",
				$"Reveal the {ContextGaps.Step} lines above the next change",
				() => Replace(gap, ContextGaps.RevealBottom(gap, ContextGaps.Step))));
			row.Children.Add(steps);
		}
		var all = Step(BothArrow, $"all {gap.HiddenCount}",
			"Reveal every hidden line here",
			() => Replace(gap, null));
		// As tall as the pair beside it, so the three read as one group of choices.
		all.Height = double.NaN;
		all.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
		all.Padding = new Avalonia.Thickness(6, 0);
		row.Children.Add(all);
		row.Children.Add(new TextBlock {
			Text = $"{gap.HiddenCount} unchanged lines",
			FontSize = 11,
			Opacity = 0.6,
			Margin = new Avalonia.Thickness(6, 0, 0, 0),
			VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
		});
		// No background and no vertical offset of its own: the row it sits on is painted by
		// ContextGapChrome, which reaches across the gutters too - an inline control can only
		// cover the text - and CenterBars puts this on the middle of it.
		//
		// The negative margin is what keeps the row close to its content. A row holding an
		// inline object comes out as tall as the object plus a whole text baseline, all of it
		// empty; reporting a shorter control hands that space back, and the buttons then draw
		// over the margin they gave up. Half above and half below keeps them centered.
		return new Border {
			Padding = new Avalonia.Thickness(4, 0),
			Margin = new Avalonia.Thickness(0, -slackPerEdge, 0, -slackPerEdge),
			Child = row,
		};

		// The arrow says what the button does and the count says how far, so it is drawn a size
		// larger than the digits beside it - and drawn rather than written, because the UI font
		// has no arrows and what the fallback font supplies ignores the size asked for.
		static Button Step(string arrow, string count, string tip, Action click)
		{
			var label = new StackPanel {
				Orientation = Avalonia.Layout.Orientation.Horizontal,
				Spacing = 3,
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
			};
			var glyph = new Avalonia.Controls.Shapes.Path {
				Data = Geometry.Parse(arrow),
				StrokeThickness = 1.2,
				StrokeLineCap = PenLineCap.Round,
				StrokeJoin = PenLineJoin.Round,
				Width = 10,
				Height = 13,
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
			};
			glyph.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Data.Binding("Foreground") {
				RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) {
					AncestorType = typeof(Button),
				},
			});
			label.Children.Add(glyph);
			label.Children.Add(new TextBlock {
				Text = count,
				FontSize = 10,
				VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
			});
			// Flat, like the window's other icon buttons: a chrome-less surface that only
			// lights up under the pointer. Three framed buttons in a row of code read as a
			// dialog dropped into the file, and their borders and padding are the vertical
			// space that pushed the row away from being a line of text.
			var button = new Button {
				Classes = { "tool" },
				Content = label,
				Height = 16,
				MinHeight = 0,
				Padding = new Avalonia.Thickness(4, 0),
				HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
				VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
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
	public Func<ContextGap, double, Control>? BarFactory { get; set; }

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
		// A row holding an inline object comes out as tall as the object plus a whole text
		// baseline, all of that empty. The control gives it back through a negative margin,
		// half at each edge, so the band ends up about as tall as what it holds.
		double slackPerEdge = CurrentContext.TextView.DefaultBaseline / 2;
		return end <= offset ? null : new InlineObjectElement(end - offset, BarFactory(gap, slackPerEdge));
	}
}

/// <summary>
/// The row a context gap's control sits on: a tint between two hairlines, drawn from the far
/// left of the gutters to the right edge so the bar reads as one band across the pane. The
/// control itself can only cover the text, so everything left of it - the line numbers, and
/// any other margin - paints the same row through <see cref="Draw"/>.
/// </summary>
public static class ContextGapChrome
{
	public static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80));
	public static readonly IBrush Border = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

	public static void Draw(DrawingContext context, double y, double width, double height)
	{
		context.FillRectangle(Fill, new Avalonia.Rect(0, y, width, height));
		context.FillRectangle(Border, new Avalonia.Rect(0, y, width, 1));
		context.FillRectangle(Border, new Avalonia.Rect(0, y + height - 1, width, 1));
	}

	/// <summary>Paints every gap row a gutter or the text view currently shows, across the
	/// width given. Called first, so what the caller draws for those lines lands on top.</summary>
	public static void DrawRows(DrawingContext context, TextView? textView, double width,
		Func<int, bool>? isGapRow)
	{
		if (textView is null || !textView.VisualLinesValid || isGapRow is null)
			return;
		foreach (var visualLine in textView.VisualLines)
		{
			if (isGapRow(visualLine.FirstDocumentLine.LineNumber))
				Draw(context, visualLine.VisualTop - textView.VerticalOffset, width, visualLine.Height);
		}
	}
}

/// <summary>Paints the gap rows across the text view, under the control drawn on them.</summary>
sealed class ContextGapBackgroundRenderer(Func<int, bool> hasBar) : IBackgroundRenderer
{
	public KnownLayer Layer => KnownLayer.Background;

	public void Draw(TextView textView, DrawingContext drawingContext)
		=> ContextGapChrome.DrawRows(drawingContext, textView, textView.Bounds.Width, hasBar);
}

/// <summary>
/// The editor's folding gutter, painting the gap rows before its markers. It sits between the
/// line numbers and the text, so without it the band the gap control reads as would have a
/// notch in it. Installed in place of the margin FoldingManager.Install adds, which is the
/// only part of that installation this replaces.
/// </summary>
public sealed class ContextGapFoldingMargin : AvaloniaEdit.Folding.FoldingMargin
{
	public Func<int, bool>? IsContextGapRow { get; set; }

	public override void Render(DrawingContext context)
	{
		ContextGapChrome.DrawRows(context, TextView, Bounds.Width, IsContextGapRow);
		base.Render(context);
	}

	/// <summary>Swaps the folding gutter of a text area for one that paints the gap rows.</summary>
	public static void Install(AvaloniaEdit.Editing.TextArea area,
		AvaloniaEdit.Folding.FoldingManager manager, Func<int, bool> isGapRow)
	{
		for (int i = 0; i < area.LeftMargins.Count; i++)
		{
			if (area.LeftMargins[i] is not AvaloniaEdit.Folding.FoldingMargin
				|| area.LeftMargins[i] is ContextGapFoldingMargin)
			{
				continue;
			}
			area.LeftMargins.RemoveAt(i);
			area.LeftMargins.Insert(i, new ContextGapFoldingMargin {
				FoldingManager = manager,
				IsContextGapRow = isGapRow,
			});
			return;
		}
	}
}
