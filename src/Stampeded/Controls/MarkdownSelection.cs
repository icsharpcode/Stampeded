using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using ColorTextBlock.Avalonia;

namespace Stampeded.Controls;

/// <summary>
/// Selecting text in rendered markdown, and copying it.
///
/// The renderer can hold a selection and paint one - every block offers Select, ClearSelection
/// and GetSelectedText - but nothing drives them: its pointer handling is about hovering and
/// following links, so a description or a comment could be read and not quoted. A drag is
/// turned into a selection here instead, across blocks, because markdown is drawn as one
/// control per paragraph, list item and code block and a reader dragging over three of them
/// means all three.
/// </summary>
public sealed class MarkdownSelection
{
	readonly Control root;
	CTextBlock? anchorBlock;
	TextPointer? anchorPointer;

	/// <summary>
	/// Watches a subtree for the gestures. Attached to the page rather than to the markdown
	/// itself: the blocks never take focus, so the copy gesture arrives wherever the reader
	/// left it, and a press outside the text is what says the selection is over.
	/// </summary>
	public static void Enable(Control root) => new MarkdownSelection(root);

	MarkdownSelection(Control root)
	{
		this.root = root;
		// Bubbling, and not for handled events: a press the renderer took for a link is a
		// link being followed, not the start of a drag.
		root.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Bubble);
		root.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Bubble);
		root.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble);
		root.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
	}

	void OnPressed(object? sender, PointerPressedEventArgs e)
	{
		if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed)
			return;
		foreach (var block in Blocks())
			block.ClearSelection();
		anchorBlock = null;
		anchorPointer = null;
		var point = e.GetPosition(root);
		if (BlockAt(point) is not { } pressed)
			return;
		anchorBlock = pressed;
		anchorPointer = PointerIn(pressed, point);
		// Held by the page for the rest of the drag, so a selection can be dragged past the
		// block it started in - which is the only way to select more than one paragraph.
		e.Pointer.Capture(root);
	}

	void OnMoved(object? sender, PointerEventArgs e)
	{
		if (anchorBlock is null || anchorPointer is null
			|| !e.GetCurrentPoint(root).Properties.IsLeftButtonPressed)
		{
			return;
		}
		var point = e.GetPosition(root);
		if ((BlockAt(point) ?? NearestBlock(point)) is not { } current)
			return;
		Apply(current, PointerIn(current, point));
	}

	void OnReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (anchorBlock is not null)
			e.Pointer.Capture(null);
	}

	void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
			return;
		string text = SelectedText();
		if (text.Length == 0)
			return;
		TopLevel.GetTopLevel(root)?.Clipboard?.SetTextAsync(text);
		e.Handled = true;
	}

	/// <summary>What is selected, joined the way the blocks are stacked.</summary>
	public string SelectedText()
		=> string.Join("\n", Blocks().Select(b => b.GetSelectedText()).Where(t => !string.IsNullOrEmpty(t)));

	/// <summary>
	/// Paints the selection from the block the drag started in to the block it is over: the
	/// two ends carry a pointer each and everything between them is whole.
	/// </summary>
	void Apply(CTextBlock current, TextPointer currentPointer)
	{
		var blocks = Blocks();
		int anchorIndex = blocks.IndexOf(anchorBlock!);
		int currentIndex = blocks.IndexOf(current);
		if (anchorIndex < 0 || currentIndex < 0)
			return;
		int first = Math.Min(anchorIndex, currentIndex);
		int last = Math.Max(anchorIndex, currentIndex);
		// Which end of the drag lies in the upper block: dragging upwards puts the pointer
		// there and the anchor at the bottom.
		var upper = anchorIndex <= currentIndex ? anchorPointer! : currentPointer;
		var lower = anchorIndex <= currentIndex ? currentPointer : anchorPointer!;
		for (int i = 0; i < blocks.Count; i++)
		{
			var block = blocks[i];
			if (i < first || i > last)
				block.ClearSelection();
			else if (first == last)
				block.Select(anchorPointer!, currentPointer);
			else
				block.Select(i == first ? upper : block.GetBegin(), i == last ? lower : block.GetEnd());
		}
	}

	List<CTextBlock> Blocks() => [.. root.GetVisualDescendants().OfType<CTextBlock>()];

	/// <summary>The block under a point of the page, if the point is on one.</summary>
	CTextBlock? BlockAt(Point point)
		=> Blocks().FirstOrDefault(b => Area(b) is { } area && area.Contains(point));

	/// <summary>
	/// The block a point outside every block belongs to: the one it is beside, else the last
	/// one above it. Dragging over the space between two paragraphs, or past the end of the
	/// text, still means everything the pointer has passed.
	/// </summary>
	CTextBlock? NearestBlock(Point point)
	{
		CTextBlock? above = null;
		foreach (var block in Blocks())
		{
			if (Area(block) is not { } area)
				continue;
			if (point.Y < area.Top)
				return above ?? block;
			above = block;
		}
		return above;
	}

	Rect? Area(CTextBlock block)
		=> block.IsVisible && block.TranslatePoint(default, root) is { } origin
			? new Rect(origin, block.Bounds.Size)
			: null;

	/// <summary>Where in a block a point of the page falls, in the block's own coordinates -
	/// which is what the renderer's hit test speaks.</summary>
	TextPointer PointerIn(CTextBlock block, Point pagePoint)
	{
		var origin = block.TranslatePoint(default, root) ?? default;
		return block.CalcuatePointerFrom(pagePoint.X - origin.X, pagePoint.Y - origin.Y);
	}
}
