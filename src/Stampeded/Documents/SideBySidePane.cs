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

	readonly Avalonia.Threading.DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
	Point lastPointerPosition;
	AvaloniaEdit.Document.TextLocation? lastHoverAt;

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
		// Press records the position and release compares against it, so dragging a
		// selection across a link does not navigate away when the button comes up. The
		// release must be handled on the TextArea: AvaloniaEdit captures the pointer, and
		// a captured release is raised on the capturing control.
		editor.TextArea.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
			RoutingStrategies.Tunnel, handledEventsToo: true);
		editor.TextArea.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased,
			RoutingStrategies.Bubble, handledEventsToo: true);
		// What a symbol is, without going anywhere: the same quick info the unified view shows,
		// and simpler here - a pane holds one blob, so the line under the pointer belongs to a
		// known side and needs no working out.
#if DEBUG
		_ = new PointerCrossHairRenderer(editor.TextArea.TextView);
#endif
		editor.TextArea.TextView.PointerMoved += OnPointerMoved;
		editor.TextArea.TextView.PointerExited += (_, _) => CancelHover();
		hoverTimer.Tick += (_, _) => {
			hoverTimer.Stop();
			ShowHoverAsync().HandleExceptions();
		};
	}

	void OnPointerMoved(object? sender, PointerEventArgs e)
	{
		var point = e.GetPosition(editor);
		UpdateTextCursor(e);
		// Only a pointer now over different text closes what is open: the tooltip appearing
		// under it is a pointer event too, and acting on that one never lets a tooltip be read.
		if (!Stampeded.Editor.HoverPointer.PointsElsewhere(editor, point, ref lastHoverAt))
			return;
		lastPointerPosition = point;
		Avalonia.Controls.ToolTip.SetIsOpen(editor, false);
		hoverTimer.Stop();
		hoverTimer.Start();
	}

	bool foldCursorActive;

	void UpdateTextCursor(PointerEventArgs e)
		=> foldCursorActive = FoldCursor.Update(editor.TextArea.TextView, e, foldCursorActive);

	void CancelHover()
	{
		hoverTimer.Stop();
		Avalonia.Controls.ToolTip.SetIsOpen(editor, false);
	}

	/// <summary>Quick info for whatever the pointer came to rest on, or nothing at all: a
	/// tooltip that says "no symbol here" is a tooltip in the way.
	///
	/// Every way of coming up empty is logged, once per reason. A hover that produces nothing
	/// is indistinguishable from a hover that never ran, and the difference is the whole
	/// question when someone reports that this does not work.</summary>
	async Task ShowHoverAsync()
	{
		string side = oldSide ? "left" : "right";
		if (App.Workspace is not { } ws || relPath.Length == 0)
		{
			HoverLog($"hover({side}): no document in this pane yet");
			return;
		}
		if (editor.GetPositionFromPoint(lastPointerPosition) is not { } position)
		{
			HoverLog($"hover({side}): pointer is past the end of the text");
			return;
		}
		int docLine = position.Line;
		if (docLine < 1 || docLine > tags.Count)
		{
			HoverLog($"hover({side}): row {docLine} is outside the {tags.Count} this pane has");
			return;
		}
		int blobLine = oldSide ? tags[docLine - 1].OldLine : tags[docLine - 1].NewLine;
		// A filler row belongs to neither blob, so there is nothing under the pointer to ask
		// about.
		if (blobLine <= 0)
		{
			HoverLog($"hover({side}): row {docLine} is filler - the other side's line");
			return;
		}
		var semantics = ws.SemanticsFor(oldSide);
		if (semantics is not { State: SemanticState.Ready or SemanticState.SyntaxOnly })
		{
			HoverLog($"hover({side}): semantics are {semantics?.State.ToString() ?? "not loaded"}");
			return;
		}
		if (await semantics.GetPositionAsync(relPath, blobLine, position.Column, CancellationToken.None) is not { } offset)
		{
			HoverLog($"hover({side}): {relPath}:{blobLine} is not a line the compilation has");
			return;
		}
		if (await semantics.GetQuickInfoAsync(relPath, offset, CancellationToken.None) is not { Length: > 0 } text)
		{
			HoverLog($"hover({side}): nothing to say about {relPath}:{blobLine},{position.Column}");
			return;
		}
		HoverLog($"hover({side}): {relPath}:{blobLine},{position.Column} -> {text.Split('\n')[0]}");
		Avalonia.Controls.ToolTip.SetTip(editor,
			Stampeded.Editor.QuickInfoView.For(text, relPath, editor.FontFamily));
		Avalonia.Controls.ToolTip.SetIsOpen(editor, true);
	}

	string? lastHoverLog;

	/// <summary>Logs a hover outcome, skipping a repeat of the one before it: the pointer
	/// resting still asks again every time it twitches, and the log is for reading.</summary>
	void HoverLog(string message)
	{
		if (message == lastHoverLog)
			return;
		lastHoverLog = message;
		Core.Infra.CliLog.Write("semantics", message);
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
