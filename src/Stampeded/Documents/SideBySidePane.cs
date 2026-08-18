using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;

using Stampeded.Core.Diff;
using Stampeded.Core.Roslyn;
using Stampeded.Diff;
using Stampeded.Editor;

namespace Stampeded.Documents;

/// <summary>
/// The semantic and navigation layer of one side-by-side pane. Each pane shows exactly
/// one blob, so a document line maps to a blob line by reading that side's number off the
/// tag - no interleaving to disambiguate, unlike the unified view. Filler rows belong to
/// neither blob and are inert.
/// </summary>
sealed class SideBySidePane
{
	const double MinimumDragDistance = 4;

	readonly ReviewTextEditor editor;
	readonly bool oldSide;
	readonly ReferenceElementGenerator referenceGenerator = new(_ => true);
	readonly Dictionary<int, int> blobToDocLine = [];

	RichTextColorizer? colorizer;
	IReadOnlyList<DiffLineTag> tags = [];
	string pairText = "";
	string relPath = "";
	string dockableId = "";
	// Where the press happened and which modifiers were held for it; the release only
	// measures the distance, since a modifier pressed mid-click was not part of the gesture.
	(Point Position, KeyModifiers Modifiers)? clickStart;

	public SideBySidePane(ReviewTextEditor editor, bool oldSide)
	{
		this.editor = editor;
		this.oldSide = oldSide;
		editor.TextArea.TextView.ElementGenerators.Add(referenceGenerator);
		referenceGenerator.QueryCursor = (element, segment, modifiers) =>
			element.Cursor = new Cursor(
				modifiers.HasFlag(KeyModifiers.Control) && segment.Kind == ReferenceMode.Link
					? StandardCursorType.Hand
					: StandardCursorType.Ibeam);
		editor.TextArea.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
		// Press records the position and release compares against it, so dragging a
		// selection across a link does not navigate away when the button comes up. The
		// release must be handled on the TextArea: AvaloniaEdit captures the pointer, and
		// a captured release is raised on the capturing control.
		editor.TextArea.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
			RoutingStrategies.Tunnel, handledEventsToo: true);
		editor.TextArea.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
			RoutingStrategies.Bubble, handledEventsToo: true);
	}

	public void SetDocument(SideBySideDocumentViewModel vm)
	{
		tags = oldSide ? vm.Pair.LeftTags : vm.Pair.RightTags;
		pairText = vm.Pair.GetSideText(oldSide).Text;
		relPath = oldSide ? vm.File.OldPath : vm.File.Path;
		dockableId = vm.Id ?? "";
		blobToDocLine.Clear();
		for (int i = 0; i < tags.Count; i++)
		{
			int blobLine = oldSide ? tags[i].OldLine : tags[i].NewLine;
			if (blobLine > 0)
				blobToDocLine[blobLine] = i + 1;
		}
		referenceGenerator.References = null;
		RemoveColorizer();
	}

	/// <summary>Puts the caret on a line of this pane's blob, and says whether that line is
	/// shown here at all - the other side's rows are filler in this one.</summary>
	public bool MoveCaretToBlobLine(int blobLine)
	{
		if (!blobToDocLine.TryGetValue(blobLine, out int docLine) || docLine > editor.Document.LineCount)
			return false;
		editor.TextArea.Caret.Line = docLine;
		editor.TextArea.Caret.Column = 1;
		editor.ScrollToLine(docLine);
		editor.TextArea.Focus();
		return true;
	}

	/// <summary>Caret as a blob position, or null on a filler row.</summary>
	public (string RelPath, int Line, int Column, bool OldSide)? CaretBlobPosition()
	{
		int docLine = editor.TextArea.Caret.Line;
		if (docLine < 1 || docLine > tags.Count || relPath.Length == 0)
			return null;
		int blobLine = oldSide ? tags[docLine - 1].OldLine : tags[docLine - 1].NewLine;
		return blobLine > 0 ? (relPath, blobLine, editor.TextArea.Caret.Column, oldSide) : null;
	}

	public async Task RefreshSemanticsAsync()
	{
		if (App.Workspace is not { } ws || relPath.Length == 0)
			return;
		var semantics = ws.SemanticsFor(oldSide);
		if (semantics is not { State: SemanticState.Ready or SemanticState.SyntaxOnly })
			return;
		var expectedTags = tags;
		// Token positions are offsets into the text they were computed from; against any
		// other revision of the file they paint the wrong spans.
		if (await semantics.GetDocumentTextAsync(relPath, CancellationToken.None) is not { } loaded
			|| !string.Equals(
				loaded.ReplaceLineEndings("\n").TrimEnd('\n'),
				pairText.ReplaceLineEndings("\n").TrimEnd('\n'),
				StringComparison.Ordinal))
		{
			return;
		}
		var tokens = await semantics.GetSemanticTokensAsync(relPath, CancellationToken.None);
		if (!ReferenceEquals(expectedTags, tags))
			return; // the pane was pointed at another document while we computed

		var rich = new RichTextModel();
		var segments = new TextSegmentCollection<ReferenceSegment>();
		foreach (var token in tokens)
		{
			if (!blobToDocLine.TryGetValue(token.Line, out int docLine) || docLine > editor.Document.LineCount)
				continue;
			var line = editor.Document.GetLineByNumber(docLine);
			if (token.Column - 1 + token.Length > line.Length)
				continue;
			int offset = line.Offset + token.Column - 1;
			if (ClassificationColors.Get(token.Classification) is { } color)
				rich.SetHighlighting(offset, token.Length, color);
			segments.Add(new ReferenceSegment {
				StartOffset = offset,
				Length = token.Length,
				Kind = ReferenceMode.Link,
				Reference = new TokenRef(oldSide, token.Line, token.Column),
			});
		}
		RemoveColorizer();
		colorizer = new RichTextColorizer(rich);
		editor.TextArea.TextView.LineTransformers.Add(colorizer);
		referenceGenerator.References = segments;
		editor.TextArea.TextView.Redraw();
	}

	void RemoveColorizer()
	{
		if (colorizer is not null)
			editor.TextArea.TextView.LineTransformers.Remove(colorizer);
		colorizer = null;
	}

	void OnKeyDown(object? sender, KeyEventArgs e)
	{
		switch (e.Key, e.KeyModifiers)
		{
			case (Key.F12, KeyModifiers.None):
				NavigateToDefinition();
				e.Handled = true;
				break;
			case (Key.F12, KeyModifiers.Shift):
				ShowReferences();
				e.Handled = true;
				break;
		}
	}

	void OnPointerPressed(object? sender, PointerPressedEventArgs e)
		=> clickStart = e.GetCurrentPoint(editor).Properties.IsLeftButtonPressed
			? (e.GetPosition(editor), e.KeyModifiers)
			: null;

	void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		var start = clickStart;
		clickStart = null;
		// The modifiers of the press, not of the release: Ctrl pressed while the button is
		// already down was not part of the gesture that started.
		if (e.InitialPressMouseButton != MouseButton.Left || start is null
			|| start.Value.Modifiers != KeyModifiers.Control)
		{
			return;
		}
		var delta = e.GetPosition(editor) - start.Value.Position;
		if (Math.Abs(delta.X) >= MinimumDragDistance || Math.Abs(delta.Y) >= MinimumDragDistance)
			return;
		editor.TextArea.ClearSelection();
		NavigateToDefinition();
	}

	public void NavigateToDefinition()
	{
		if (CaretBlobPosition() is not { } pos)
			return;
		App.Workspace?.NavigateToDefinitionAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide,
			new ReviewWorkspace.NavEntryOrigin(dockableId, pos.Line, pos.OldSide)).HandleExceptions();
	}

	public void ShowReferences()
	{
		if (CaretBlobPosition() is { } pos)
			App.Workspace?.ShowReferencesAtAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide).HandleExceptions();
	}
}
