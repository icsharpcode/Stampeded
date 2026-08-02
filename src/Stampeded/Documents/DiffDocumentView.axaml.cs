using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;

using Stampeded.Core.Diff;
using Stampeded.Core.Roslyn;
using Stampeded.Diff;
using Stampeded.Editor;

namespace Stampeded.Documents;

/// <summary>Payload of a clickable reference span: which side and blob position it names.</summary>
sealed record TokenRef(bool OldSide, int Line, int Column);

public partial class DiffDocumentView : UserControl
{
	// Unchanged context kept visible around each hunk when folding the rest.
	const int FoldContext = 3;

	static readonly Color OccurrenceColor = Color.Parse("#5A86C691");
	static readonly Color DefinitionOccurrenceColor = Color.Parse("#7A86C691");

	readonly DiffLineNumberMargin margin = new();
	readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
	readonly ReferenceElementGenerator referenceGenerator = new(_ => true);
	readonly TextMarkerService markers;
	Avalonia.Point lastPointerPosition;
	FoldingManager? foldingManager;
	DiffDocumentModel? model;
	DiffDocumentViewModel? viewModel;
	RichTextColorizer? semanticColorizer;
	bool semanticsRefreshQueued;

	public DiffDocumentView()
	{
		InitializeComponent();
		SearchPanel.Install(Editor);
		Editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => model?.Tags));
		markers = new TextMarkerService(Editor.TextArea.TextView);
		Editor.TextArea.TextView.BackgroundRenderers.Add(markers);
		Editor.TextArea.TextView.ElementGenerators.Add(referenceGenerator);
		Editor.TextArea.LeftMargins.Insert(0, margin);
		Editor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
		Editor.TextArea.TextView.AddHandler(PointerReleasedEvent, OnTextViewPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
		Editor.TextArea.AddHandler(PointerPressedEvent, OnPointerPressedForContextMenu, RoutingStrategies.Tunnel);
		AddHandler(GotFocusEvent, (_, _) => ActiveView = this, RoutingStrategies.Bubble, handledEventsToo: true);
		Editor.TextArea.TextView.PointerMoved += OnPointerMovedForHover;
		Editor.TextArea.TextView.PointerExited += (_, _) => CancelHover();
		hoverTimer.Tick += OnHoverTimerTick;
	}

	/// <summary>The most recently attached/focused diff view; menu commands route here.</summary>
	public static DiffDocumentView? ActiveView { get; private set; }

	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		ActiveView = this;
		if (App.Workspace is { } ws)
			ws.SemanticsChanged += OnSemanticsChanged;
		Themes.ThemeManager.Current.ThemeChanged += OnThemeChangedForSemantics;
		QueueSemanticsRefresh();
	}

	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		if (ActiveView == this)
			ActiveView = null;
		if (App.Workspace is { } ws)
			ws.SemanticsChanged -= OnSemanticsChanged;
		Themes.ThemeManager.Current.ThemeChanged -= OnThemeChangedForSemantics;
		base.OnDetachedFromVisualTree(e);
	}


	#region Menu / context-menu commands

	public void JumpToHunkCommand(int direction) => JumpToHunk(direction);

	public void CommentAtCaretCommand()
	{
		if (CaretBlobPosition() is not { } pos)
			return;
		var docLine = Editor.Document.GetLineByNumber(Editor.TextArea.Caret.Line);
		string text = Editor.Document.GetText(docLine.Offset, docLine.Length);
		App.Workspace?.BeginComment(new ReviewWorkspace.CommentTarget(pos.RelPath, pos.OldSide, pos.Line, text));
	}

	public void ToggleBlameCommand() => ToggleBlameAsync().HandleExceptions();
	public void GoToDefinitionCommand() => NavigateToDefinitionAtCaret();
	public void FindReferencesCommand() => ShowReferencesAtCaret();
	public void HighlightOccurrencesCommand() => HighlightOccurrencesAtCaretAsync().HandleExceptions();

	void OnCtxGoToDefinition(object? s, RoutedEventArgs e) => GoToDefinitionCommand();
	void OnCtxFindReferences(object? s, RoutedEventArgs e) => FindReferencesCommand();
	void OnCtxHighlightOccurrences(object? s, RoutedEventArgs e) => HighlightOccurrencesCommand();
	void OnCtxNextHunk(object? s, RoutedEventArgs e) => JumpToHunk(1);
	void OnCtxPrevHunk(object? s, RoutedEventArgs e) => JumpToHunk(-1);
	void OnCtxToggleBlame(object? s, RoutedEventArgs e) => ToggleBlameCommand();
	void OnCtxComment(object? s, RoutedEventArgs e) => CommentAtCaretCommand();
	void OnCtxCopy(object? s, RoutedEventArgs e) => Editor.Copy();

	void OnPointerPressedForContextMenu(object? sender, PointerPressedEventArgs e)
	{
		// Right-click moves the caret to the click point first, so the context-menu
		// commands act on the symbol that was clicked, matching IDE behavior.
		if (!e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed)
			return;
		var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
		if (position is null)
			return;
		Editor.TextArea.Caret.Line = position.Value.Line;
		Editor.TextArea.Caret.Column = position.Value.Column;
	}

	#endregion

	void OnSemanticsChanged() => Dispatcher.UIThread.Post(QueueSemanticsRefresh);

	void OnThemeChangedForSemantics(object? sender, EventArgs e) => QueueSemanticsRefresh();

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (viewModel is not null)
			viewModel.CaretRequested -= OnCaretRequested;
		if (DataContext is not DiffDocumentViewModel vm)
			return;
		viewModel = vm;
		vm.CaretRequested += OnCaretRequested;
		model = vm.Model;
		Editor.SyntaxHighlighting = HighlightingService.GetByExtension(Path.GetExtension(vm.File.Path));
		Editor.Text = vm.Model.Text;
		margin.Tags = vm.Model.Tags;
		margin.InvalidateMeasure();
		Overview.Attach(Editor, vm.Model.Tags);
		InstallFoldings(vm.Model);
		referenceGenerator.References = null;
		markers.RemoveAll(_ => true);
		QueueSemanticsRefresh();
		if (vm.TakePendingCaretLine() is int line)
			Dispatcher.UIThread.Post(() => MoveCaretToLine(line));
	}

	#region Semantic layer (colors + clickable spans)

	void QueueSemanticsRefresh()
	{
		if (semanticsRefreshQueued)
			return;
		semanticsRefreshQueued = true;
		Dispatcher.UIThread.Post(() => {
			semanticsRefreshQueued = false;
			RefreshSemanticsAsync().HandleExceptions();
		}, DispatcherPriority.Background);
	}

	async Task RefreshSemanticsAsync()
	{
		if (model is null || viewModel is null || App.Workspace is not { } ws)
			return;
		var m = model;
		var vm = viewModel;

		var headSem = ws.SemanticsFor(oldSide: false);
		var baseSem = ws.SemanticsFor(oldSide: true);
		var headTokens = headSem is { State: SemanticState.Ready or SemanticState.SyntaxOnly }
			? await headSem.GetSemanticTokensAsync(vm.File.Path, CancellationToken.None)
			: [];
		bool hasRemoved = m.Tags.Any(t => t.Kind == DiffLineKind.Removed);
		var baseTokens = hasRemoved && baseSem is { State: SemanticState.Ready or SemanticState.SyntaxOnly }
			? await baseSem.GetSemanticTokensAsync(vm.File.OldPath, CancellationToken.None)
			: [];
		if (model != m || viewModel != vm)
			return; // document changed while we were computing

		var rich = new RichTextModel();
		var segments = new TextSegmentCollection<ReferenceSegment>();
		AddTokens(rich, segments, headTokens, oldSide: false);
		AddTokens(rich, segments, baseTokens, oldSide: true);

		if (semanticColorizer is not null)
			Editor.TextArea.TextView.LineTransformers.Remove(semanticColorizer);
		semanticColorizer = new RichTextColorizer(rich);
		Editor.TextArea.TextView.LineTransformers.Add(semanticColorizer);
		referenceGenerator.References = segments;
		Editor.TextArea.TextView.Redraw();
	}

	void AddTokens(RichTextModel rich, TextSegmentCollection<ReferenceSegment> segments,
		IReadOnlyList<SemanticToken> tokens, bool oldSide)
	{
		if (model is null)
			return;
		foreach (var token in tokens)
		{
			int? docLine = oldSide ? model.DocLineFromOldLine(token.Line) : model.DocLineFromNewLine(token.Line);
			if (docLine is null || docLine > Editor.Document.LineCount)
				continue;
			var tag = model.Tags[docLine.Value - 1];
			// Context lines exist on both sides; color them once, from the head tokens.
			if (oldSide && tag.Kind != DiffLineKind.Removed)
				continue;
			var line = Editor.Document.GetLineByNumber(docLine.Value);
			int offset = line.Offset + token.Column - 1;
			if (token.Column - 1 + token.Length > line.Length)
				continue;
			if (ClassificationColors.Get(token.Classification) is { } color)
				rich.SetHighlighting(offset, token.Length, color);
			segments.Add(new ReferenceSegment {
				StartOffset = offset,
				Length = token.Length,
				Kind = ReferenceMode.Link,
				Reference = new TokenRef(oldSide, token.Line, token.Column),
			});
		}
	}

	#endregion

	void OnCaretRequested(int docLine)
	{
		Dispatcher.UIThread.Post(() => MoveCaretToLine(docLine));
	}

	void MoveCaretToLine(int line)
	{
		if (line < 1 || line > Editor.Document.LineCount)
			return;
		int offset = Editor.Document.GetLineByNumber(line).Offset;
		if (foldingManager is not null)
		{
			foreach (var folding in foldingManager.GetFoldingsContaining(offset))
				folding.IsFolded = false;
		}
		Editor.TextArea.Caret.Line = line;
		Editor.TextArea.Caret.Column = 1;
		Editor.ScrollToLine(line);
		Editor.TextArea.Focus();
	}

	void InstallFoldings(DiffDocumentModel m)
	{
		foldingManager ??= FoldingManager.Install(Editor.TextArea);
		var foldings = new List<NewFolding>();
		int runStart = -1; // 0-based tag index of the current context run
		for (int i = 0; i <= m.Tags.Count; i++)
		{
			bool context = i < m.Tags.Count && m.Tags[i].Kind == DiffLineKind.Context;
			if (context && runStart < 0)
				runStart = i;
			else if (!context && runStart >= 0)
			{
				AddFolding(m, foldings, runStart, i - 1);
				runStart = -1;
			}
		}
		foldingManager.Clear();
		foldingManager.UpdateFoldings(foldings.OrderBy(f => f.StartOffset).ToList(), -1);
	}

	void AddFolding(DiffDocumentModel m, List<NewFolding> foldings, int firstTag, int lastTag)
	{
		// A source view (identity model) has no hunks; keep it entirely unfolded.
		if (m.Hunks.Count == 0)
			return;
		// Keep FoldContext lines visible on each side; at the document edges the whole
		// run may fold except the context adjoining the hunk.
		int foldFirst = firstTag == 0 ? firstTag : firstTag + FoldContext;
		int foldLast = lastTag == m.Tags.Count - 1 ? lastTag : lastTag - FoldContext;
		int hidden = foldLast - foldFirst + 1;
		if (hidden < 2)
			return;
		var startLine = Editor.Document.GetLineByNumber(foldFirst + 1);
		var endLine = Editor.Document.GetLineByNumber(foldLast + 1);
		foldings.Add(new NewFolding(startLine.Offset, endLine.EndOffset) {
			Name = $"... {hidden} unchanged lines",
			DefaultClosed = true,
		});
	}

	void OnEditorKeyDown(object? sender, KeyEventArgs e)
	{
		switch (e.Key, e.KeyModifiers)
		{
			case (Key.N, KeyModifiers.None):
				JumpToHunk(1);
				e.Handled = true;
				break;
			case (Key.P, KeyModifiers.None):
				JumpToHunk(-1);
				e.Handled = true;
				break;
			case (Key.OemCloseBrackets, KeyModifiers.None):
				App.Workspace?.OpenAdjacentFileAsync(1).HandleExceptions();
				e.Handled = true;
				break;
			case (Key.OemOpenBrackets, KeyModifiers.None):
				App.Workspace?.OpenAdjacentFileAsync(-1).HandleExceptions();
				e.Handled = true;
				break;
			case (Key.V, KeyModifiers.None):
				App.Workspace?.ToggleViewedAndAdvanceAsync().HandleExceptions();
				e.Handled = true;
				break;
			case (Key.F12, KeyModifiers.None):
				NavigateToDefinitionAtCaret();
				e.Handled = true;
				break;
			case (Key.F12, KeyModifiers.Shift):
				ShowReferencesAtCaret();
				e.Handled = true;
				break;
			case (Key.B, KeyModifiers.None):
				ToggleBlameAsync().HandleExceptions();
				e.Handled = true;
				break;
			case (Key.C, KeyModifiers.None):
				CommentAtCaretCommand();
				e.Handled = true;
				break;
			case (Key.Escape, KeyModifiers.None):
				markers.RemoveAll(_ => true);
				break;
			case (Key.Left, KeyModifiers.Alt):
				App.Workspace?.GoBackAsync().HandleExceptions();
				e.Handled = true;
				break;
			case (Key.Right, KeyModifiers.Alt):
				App.Workspace?.GoForwardAsync().HandleExceptions();
				e.Handled = true;
				break;
		}
	}

	void OnTextViewPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (e.InitialPressMouseButton != MouseButton.Left)
			return;
		// The click has already placed the caret. Ctrl+Click navigates; a plain click on
		// a symbol highlights its occurrences in this document.
		if (e.KeyModifiers == KeyModifiers.Control)
			NavigateToDefinitionAtCaret();
		else if (e.KeyModifiers == KeyModifiers.None && Editor.TextArea.Selection.IsEmpty)
			HighlightOccurrencesAtCaretAsync().HandleExceptions();
	}

	/// <summary>Caret as a blob position: head side on context/added lines, base side on
	/// removed lines (whose code only exists at the merge base).</summary>
	(string RelPath, int Line, int Column, bool OldSide)? CaretBlobPosition()
	{
		if (model is null || viewModel is null)
			return null;
		int docLine = Editor.TextArea.Caret.Line;
		if (docLine < 1 || docLine > model.Tags.Count)
			return null;
		var tag = model.Tags[docLine - 1];
		if (tag.NewLine > 0)
			return (viewModel.File.Path, tag.NewLine, Editor.TextArea.Caret.Column, false);
		if (tag.OldLine > 0)
			return (viewModel.File.OldPath, tag.OldLine, Editor.TextArea.Caret.Column, true);
		return null;
	}

	void NavigateToDefinitionAtCaret()
	{
		if (CaretBlobPosition() is not { } pos || viewModel is null)
			return;
		var origin = new ReviewWorkspace.NavEntryOrigin(viewModel.Id, Editor.TextArea.Caret.Line);
		App.Workspace?.NavigateToDefinitionAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide, origin).HandleExceptions();
	}

	void ShowReferencesAtCaret()
	{
		if (CaretBlobPosition() is not { } pos)
			return;
		App.Workspace?.ShowReferencesAtAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide).HandleExceptions();
	}

	async Task HighlightOccurrencesAtCaretAsync()
	{
		markers.RemoveAll(_ => true);
		if (CaretBlobPosition() is not { } pos || model is null || App.Workspace is not { } ws)
			return;
		var occurrences = await ws.FindOccurrencesAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide);
		foreach (var occ in occurrences)
		{
			int? docLine = pos.OldSide ? model.DocLineFromOldLine(occ.Line) : model.DocLineFromNewLine(occ.Line);
			if (docLine is null || docLine > Editor.Document.LineCount)
				continue;
			var line = Editor.Document.GetLineByNumber(docLine.Value);
			if (occ.Column - 1 + occ.Length > line.Length)
				continue;
			var marker = markers.Create(line.Offset + occ.Column - 1, occ.Length);
			marker.BackgroundColor = occ.Classification == "definition" ? DefinitionOccurrenceColor : OccurrenceColor;
		}
	}

	void JumpToHunk(int direction)
	{
		if (model is null || model.Hunks.Count == 0)
			return;
		int caretLine = Editor.TextArea.Caret.Line;
		HunkSpan? target = direction > 0
			? model.Hunks.Cast<HunkSpan?>().FirstOrDefault(h => h!.Value.FirstDocLine > caretLine)
			: model.Hunks.Cast<HunkSpan?>().LastOrDefault(h => h!.Value.FirstDocLine < caretLine);
		if (target is null)
			return;
		MoveCaretToLine(target.Value.FirstDocLine);
	}

	#region Blame

	readonly BlameMargin blameMargin = new();
	bool blameVisible;

	async Task ToggleBlameAsync()
	{
		if (blameVisible)
		{
			Editor.TextArea.LeftMargins.Remove(blameMargin);
			blameVisible = false;
			return;
		}
		if (model is null || viewModel is null || App.Workspace is not { } ws || ws.HeadSha is null)
			return;
		var m = model;
		var vm = viewModel;
		IReadOnlyList<Core.Git.BlameLine> newBlame = [];
		IReadOnlyList<Core.Git.BlameLine> oldBlame = [];
		try
		{
			if (vm.File.Kind != FileChangeKind.Deleted)
				newBlame = await ws.Git.BlameAsync(ws.HeadSha, vm.File.Path);
			if (ws.BaseSha is not null && m.Tags.Any(t => t.Kind == DiffLineKind.Removed))
				oldBlame = await ws.Git.BlameAsync(ws.BaseSha, vm.File.OldPath);
		}
		catch (Core.Infra.ToolFailedException)
		{
			return; // e.g. blaming a base-only view at head; blame is best-effort
		}
		if (model != m)
			return;
		var newByLine = newBlame.ToDictionary(b => b.FinalLine);
		var oldByLine = oldBlame.ToDictionary(b => b.FinalLine);
		var perDoc = new Core.Git.BlameLine?[m.Tags.Count];
		for (int i = 0; i < m.Tags.Count; i++)
		{
			var tag = m.Tags[i];
			perDoc[i] = tag.NewLine > 0
				? newByLine.GetValueOrDefault(tag.NewLine)
				: oldByLine.GetValueOrDefault(tag.OldLine);
		}
		blameMargin.SetLines(perDoc);
		Editor.TextArea.LeftMargins.Insert(0, blameMargin);
		blameVisible = true;
	}

	#endregion

	#region Hover tooltip

	void OnPointerMovedForHover(object? sender, PointerEventArgs e)
	{
		lastPointerPosition = e.GetPosition(Editor);
		ToolTip.SetIsOpen(Editor, false);
		hoverTimer.Stop();
		hoverTimer.Start();
	}

	void CancelHover()
	{
		hoverTimer.Stop();
		ToolTip.SetIsOpen(Editor, false);
	}

	void OnHoverTimerTick(object? sender, EventArgs e)
	{
		hoverTimer.Stop();
		ShowHoverAsync().HandleExceptions();
	}

	async Task ShowHoverAsync()
	{
		if (model is null || viewModel is null || App.Workspace is not { } ws)
			return;
		var position = Editor.GetPositionFromPoint(lastPointerPosition);
		if (position is null)
			return;
		int docLine = position.Value.Line;
		if (docLine < 1 || docLine > model.Tags.Count)
			return;
		var tag = model.Tags[docLine - 1];
		bool oldSide = tag.NewLine == 0;
		if (oldSide && tag.OldLine == 0)
			return;
		string relPath = oldSide ? viewModel.File.OldPath : viewModel.File.Path;
		int blobLine = oldSide ? tag.OldLine : tag.NewLine;
		var sem = ws.SemanticsFor(oldSide);
		if (sem is not { State: SemanticState.Ready or SemanticState.SyntaxOnly })
			return;
		int? pos = await sem.GetPositionAsync(relPath, blobLine, position.Value.Column, CancellationToken.None);
		if (pos is null)
			return;
		string? text = await sem.GetQuickInfoAsync(relPath, pos.Value, CancellationToken.None);
		if (string.IsNullOrEmpty(text))
			return;
		ToolTip.SetTip(Editor, text);
		ToolTip.SetIsOpen(Editor, true);
	}

	#endregion
}
