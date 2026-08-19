using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

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

public partial class DiffDocumentView : UserControl, IReviewDocumentView
{
	/// <summary>Every command there is: the unified layout is where they were written, and it
	/// is what the side-by-side one is measured against.</summary>
	public ReviewCommands Supported => ReviewCommands.JumpToHunk | ReviewCommands.JumpToUncovered
		| ReviewCommands.ToggleBlame | ReviewCommands.CommentAtCaret | ReviewCommands.GoToDefinition
		| ReviewCommands.FindReferences | ReviewCommands.HighlightOccurrences | ReviewCommands.ShowCallGraph
		| ReviewCommands.HistoryOfSelection | ReviewCommands.DebugHere;

	public string DocumentId => viewModel?.Id ?? "";

	static readonly Color OccurrenceColor = Color.Parse("#5A86C691");
	static readonly Color DefinitionOccurrenceColor = Color.Parse("#7A86C691");

	readonly DiffLineNumberMargin margin = new();
	readonly CoverageMargin coverageMargin = new();
	bool coverageMarginVisible;
	readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
	readonly ReferenceElementGenerator referenceGenerator = new(_ => true);
	readonly TextMarkerService markers;
	readonly Editor.ThreadElementGenerator threadGenerator = new();
	Dictionary<string, ThreadData>? threadsByKey;
	CommentThreadBox? threadBoxes;
	Avalonia.Point lastPointerPosition;
	FoldingManager? foldingManager;
	ContextGapView? contextGaps;
	List<FoldRange> structuralRanges = [];
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
		threadBoxes = new CommentThreadBox(Editor.TextArea.TextView, EditDraft, ReplyInThread);
		threadGenerator.ControlFactory = key =>
			threadsByKey?.TryGetValue(key, out var thread) == true ? threadBoxes.Build(key, thread) : null;
		Editor.TextArea.TextView.ElementGenerators.Add(threadGenerator);
		Editor.TextArea.TextView.ElementGenerators.Add(referenceGenerator);
		// Hand cursor only while Ctrl is held, matching the Ctrl+Click navigation gesture
		// (a permanent hand over every identifier promises plain-click navigation we
		// deliberately don't do - plain click places the caret / highlights occurrences).
		referenceGenerator.QueryCursor = (element, segment, modifiers) =>
			element.Cursor = new Cursor(
				modifiers.HasFlag(KeyModifiers.Control) && segment.Kind == ReferenceMode.Link
					? StandardCursorType.Hand
					: StandardCursorType.Ibeam);
		Editor.TextArea.LeftMargins.Insert(0, margin);
		FoldViewportAnchor.Install(Editor);
		contextGaps = new ContextGapView(Editor);
		contextGaps.Changed += RefreshFoldings;
		margin.IsContextGapRow = contextGaps.HasBar;
		coverageMargin.IsContextGapRow = contextGaps.HasBar;
		blameMargin.IsContextGapRow = contextGaps.HasBar;
		Editor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
		// handledEventsToo, because a TextBox that accepts returns handles Enter itself -
		// modifiers and all - before any handler declared on it runs. So Ctrl+Enter reached
		// this box as a newline and nothing else; the save is what the reader was promised.
		// The newline it inserted first goes away with the trim the save already does.
		CommentBox.AddHandler(KeyDownEvent, OnCommentBoxKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
		// Click-vs-drag discrimination (ported from ILSpy's DecompilerTextView): the press
		// only records its position; the release compares against it, so press-and-drag
		// over a link selects text instead of navigating away on release.
		Editor.TextArea.AddHandler(PointerPressedEvent, OnTextAreaPointerPressedForClick, RoutingStrategies.Tunnel, handledEventsToo: true);
		// On the TextArea, not the TextView: AvaloniaEdit captures the pointer on press,
		// and captured releases are raised on the capturing control - a TextView handler
		// never sees them (evidenced by presses logging without releases).
		Editor.TextArea.AddHandler(PointerReleasedEvent, OnTextViewPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
		Editor.TextArea.AddHandler(PointerPressedEvent, OnPointerPressedForContextMenu, RoutingStrategies.Tunnel);
		AddHandler(GotFocusEvent, (_, _) => MakeActive(), RoutingStrategies.Bubble, handledEventsToo: true);
#if DEBUG
		// Inert until switched on from the View menu; registers for the view's lifetime.
		_ = new Editor.PointerCrossHairRenderer(Editor.TextArea.TextView);
#endif
		Editor.TextArea.TextView.PointerMoved += OnPointerMovedForHover;
		Editor.TextArea.TextView.PointerExited += (_, _) => CancelHover();
		hoverTimer.Tick += OnHoverTimerTick;
		blameMargin.CommitRequested = blame =>
			App.Workspace?.OpenHistoricalDiffAsync(blame.Sha, viewModel?.File.Path ?? "").HandleExceptions();
	}

	/// <summary>The most recently attached/focused diff view; menu commands route here.</summary>
	public static DiffDocumentView? ActiveView { get; private set; }

	/// <summary>Raised when the active diff view (or its document) changes; the History
	/// pane follows it.</summary>
	public static event Action? ActiveViewChanged;

	internal DiffDocumentViewModel? ViewModel => viewModel;

	void MakeActive()
	{
		ActiveView = this;
		ActiveViewChanged?.Invoke();
	}

	/// <summary>
	/// Puts keyboard focus in the text area. The single-key review gestures (v, n, p, [, ],
	/// c, b, u) are a handler on it, so they are dead until it holds focus - which opening a
	/// document does not give it: Dock's focused dockable is a layout concept, not the
	/// keyboard's.
	/// </summary>
	public void FocusEditor() => Editor.TextArea.Focus();

	/// <summary>Where the caret is, in file coordinates and in document ones, for checks that
	/// have to know exactly rather than approximately.</summary>
	public string CaretDescription()
		=> CaretBlobPosition() is { } pos
			? $"{pos.RelPath}:{pos.Line}{(pos.OldSide ? " (base)" : "")} at document line {Editor.TextArea.Caret.Line}"
			: $"(no blob position) at document line {Editor.TextArea.Caret.Line}";

	/// <summary>
	/// The view showing a given document, for code that has the document and needs the
	/// control. <see cref="ActiveView"/> cannot answer this: Dock keeps every document's view
	/// attached and only swaps which one is visible, so "last attached or focused" is stale
	/// the moment a tab is selected without the mouse.
	/// </summary>
	public static DiffDocumentView? ViewFor(DiffDocumentViewModel document)
		=> viewsByDocument.TryGetValue(document, out var view) ? view : null;

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DiffDocumentViewModel, DiffDocumentView>
		viewsByDocument = new();

	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		ReviewViews.Register(this);
		MakeActive();
		if (App.Workspace is { } ws)
		{
			ws.SemanticsChanged += OnSemanticsChanged;
			ws.CoverageChanged += OnCoverageChanged;
			ws.Comments.Changed += OnCommentsChangedForThreads;
		}
		Themes.ThemeManager.Current.ThemeChanged += OnThemeChangedForSemantics;
		QueueSemanticsRefresh();
		OnCoverageChanged();
		OnCommentsChangedForThreads();
	}

	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		ReviewViews.Unregister(this);
		if (ActiveView == this)
			ActiveView = null;
		if (App.Workspace is { } ws)
		{
			ws.SemanticsChanged -= OnSemanticsChanged;
			ws.CoverageChanged -= OnCoverageChanged;
			ws.Comments.Changed -= OnCommentsChangedForThreads;
		}
		Themes.ThemeManager.Current.ThemeChanged -= OnThemeChangedForSemantics;
		base.OnDetachedFromVisualTree(e);
	}


	#region Menu / context-menu commands

	public bool JumpToHunkCommand(int direction) => JumpToHunk(direction);

	public void JumpToEdgeHunk(int direction)
	{
		if (model is null || model.Hunks.Count == 0)
			return;
		MoveCaretToLine(direction > 0 ? model.Hunks[0].FirstDocLine : model.Hunks[^1].FirstDocLine);
	}

	public void JumpToUncoveredCommand() => JumpToNextUncovered();

	CommentTarget? inlineCommentTarget;

	/// <summary>The draft the editor is rewriting, when it was opened on one.</summary>
	Guid? editingDraftId;

	/// <summary>
	/// Opens the editor on a draft that already exists, below the thread it belongs to. Saving
	/// rewrites that draft rather than adding another: it is the same remark, said better.
	/// </summary>
	void EditDraft(Guid draftId, string body, ThreadData thread)
	{
		int? docLine = thread.OldSide ? model?.DocLineFromOldLine(thread.BlobLine) : model?.DocLineFromNewLine(thread.BlobLine);
		if (docLine is not { } dl)
			return;
		MoveCaretToLine(dl);
		CommentAtCaretCommand(LastThreadLineAfter(dl));
		if (!CommentPopup.IsOpen)
			return;
		editingDraftId = draftId;
		CommentBox.Text = body;
		CommentBox.CaretIndex = body.Length;
	}

	public void CommentAtCaretCommand() => CommentAtCaretCommand(null);

	/// <param name="anchorLine">Document line the editor should be placed under. A reply
	/// belongs below the thread it answers, and the thread's box sits on its own line under
	/// the commented code - anchoring to the caret alone would open the editor on top of
	/// what is being replied to.</param>
	/// <param name="inReplyTo">The posted comment being answered, when this is a reply.</param>
	public void CommentAtCaretCommand(int? anchorLine, long inReplyTo = 0)
	{
		if (viewModel is { Historical: true } || CaretBlobPosition() is not { } pos)
			return;
		if (App.Workspace is { Comments.CanComment: false } local)
		{
			// Say it here rather than let the popup take text that BeginComment would drop.
			local.PostStatus("Comments need a pull request; this is a local review.");
			return;
		}
		// A fresh editor: whatever the last one held - a draft being rewritten, or text a
		// dismissal left behind - is not this comment.
		editingDraftId = null;
		CommentBox.Text = "";
		var docLine = Editor.Document.GetLineByNumber(Editor.TextArea.Caret.Line);
		string text = Editor.Document.GetText(docLine.Offset, docLine.Length);
		inlineCommentTarget = new CommentTarget(
			pos.RelPath, pos.OldSide, pos.Line, text, inReplyTo == 0 ? null : inReplyTo);
		CommentTargetText.Text = (inReplyTo == 0 ? "" : "Reply  |  ")
			+ $"{pos.RelPath}:{pos.Line}{(pos.OldSide ? " (base)" : "")}  |  {text.Trim()}";
		var view = Editor.TextArea.TextView;
		int anchorAt = Math.Clamp(anchorLine ?? Editor.TextArea.Caret.Line, 1, Editor.Document.LineCount);
		var caretPosition = new AvaloniaEdit.TextViewPosition(anchorAt, 1);
		double anchorY = ScrollToMakeRoomBelow(caretPosition);
		double marginsWidth = Editor.TextArea.LeftMargins.OfType<Avalonia.Controls.Control>().Sum(m => m.Bounds.Width);
		CommentPopup.HorizontalOffset = marginsWidth + 8;
		CommentPopup.VerticalOffset = anchorY;
		CommentPopup.IsLightDismissEnabled = true;
		CommentPopup.IsOpen = true;
		CommentBox.Focus();
	}

	/// <summary>
	/// The offset the editor box should sit at, having scrolled far enough that it fits under
	/// its anchor. Replying to a tall thread otherwise puts the box past the bottom of the
	/// diff, where it floats over the pane below - the popup is an overlay and knows nothing
	/// of the editor's bounds.
	/// </summary>
	double ScrollToMakeRoomBelow(AvaloniaEdit.TextViewPosition position)
	{
		var view = Editor.TextArea.TextView;
		double anchorY = (view.GetVisualPosition(position, VisualYPosition.LineBottom) - view.ScrollOffset).Y;
		double overflow = anchorY + CommentBoxHeight - view.Bounds.Height;
		if (overflow > 0
			&& Editor.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().FirstOrDefault() is { } scroll)
		{
			double max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
			double target = Math.Clamp(scroll.Offset.Y + overflow, 0, max);
			double moved = target - scroll.Offset.Y;
			scroll.Offset = new Avalonia.Vector(scroll.Offset.X, target);
			anchorY -= moved;
		}
		return anchorY;
	}

	/// <summary>Height the comment editor needs, as laid out in the view's markup.</summary>
	const double CommentBoxHeight = 150;


	/// <summary>
	/// The last of the thread rows reserved under a code line, or the line itself when it
	/// carries none. Threads are spliced in as synthetic lines below the code they comment
	/// on, so this is the bottom of everything already said there.
	/// </summary>
	int LastThreadLineAfter(int docLine)
	{
		if (model is null)
			return docLine;
		int last = docLine;
		for (int line = docLine + 1; line <= model.Tags.Count && model.Tags[line - 1].Kind == DiffLineKind.Comment; line++)
			last = line;
		return last;
	}

	/// <summary>Answers a thread: the caret goes to the line it hangs on and the comment
	/// editor opens below everything already said there.</summary>
	void ReplyInThread(ThreadData thread, long replyTo)
	{
		int? docLine = thread.OldSide ? model?.DocLineFromOldLine(thread.BlobLine) : model?.DocLineFromNewLine(thread.BlobLine);
		if (docLine is not { } dl)
			return;
		MoveCaretToLine(dl);
		CommentAtCaretCommand(LastThreadLineAfter(dl), replyTo);
	}

	void OnCommentsChangedForThreads()
	{
		Dispatcher.UIThread.Post(RebuildThreads);
	}

	/// <summary>Recomputes the comment threads of this file and re-splices the document
	/// with a reserved line per thread; caret and view position are restored via the
	/// blob mapping, which survives the reflow.</summary>
	void RebuildThreads()
	{
		if (viewModel is null || viewModel.Historical || App.Workspace is not { } ws)
			return;
		var threads = CommentThreads.For(ws, viewModel.File);
		threadsByKey = threads.Count == 0 ? null : threads;
		var anchors = CommentThreads.Anchors(threads);
		var target = anchors.Count == 0
			? viewModel.PristineModel
			: viewModel.PristineModel.WithThreadLines(anchors);
		if (ReferenceEquals(target, model) || target.Text == model?.Text)
		{
			Editor.TextArea.TextView.Redraw();
			return;
		}
		var caret = CaretBlobPosition();
		var expandedFolds = CaptureExpandedFolds();
		var openedGaps = CaptureGaps();
		viewModel.ReplaceModel(target);
		model = target;
		ApplyModelToEditor(target);
		RestoreExpandedFolds(expandedFolds);
		RestoreGaps(openedGaps, target);
		if (caret is { } restore)
		{
			// Restore position without focusing: a background tab grabbing focus would
			// make the dock activate it (e.g. stealing the front from the Overview).
			int? docLine = restore.OldSide ? target.DocLineFromOldLine(restore.Line) : target.DocLineFromNewLine(restore.Line);
			if (docLine is { } dl && dl >= 1 && dl <= Editor.Document.LineCount)
			{
				int offset = Editor.Document.GetLineByNumber(dl).Offset;
				if (foldingManager is not null)
				{
					foreach (var folding in foldingManager.GetFoldingsContaining(offset))
						folding.IsFolded = false;
				}
				Editor.TextArea.Caret.Line = dl;
				Editor.TextArea.Caret.Column = 1;
				Editor.ScrollToLine(dl);
			}
		}
	}

	void ApplyMarginCursors()
	{
		foreach (var marginControl in Editor.TextArea.LeftMargins.OfType<Avalonia.Controls.Control>())
			marginControl.Cursor = new Cursor(StandardCursorType.Arrow);
	}

	/// <summary>Blob positions (side, line) of folds the user has expanded; fold state is
	/// keyed by content so it survives the re-splices that renumber document lines.</summary>
	List<(bool OldSide, int Line)> CaptureExpandedFolds()
	{
		var expanded = new List<(bool, int)>();
		if (foldingManager is null || model is null)
			return expanded;
		foreach (var folding in foldingManager.AllFoldings.Where(f => !f.IsFolded))
		{
			int docLine = Editor.Document.GetLineByOffset(folding.StartOffset).LineNumber;
			if (docLine < 1 || docLine > model.Tags.Count)
				continue;
			var tag = model.Tags[docLine - 1];
			if (tag.NewLine > 0)
				expanded.Add((false, tag.NewLine));
			else if (tag.OldLine > 0)
				expanded.Add((true, tag.OldLine));
		}
		return expanded;
	}

	void RestoreExpandedFolds(List<(bool OldSide, int Line)> expanded)
	{
		if (foldingManager is null || model is null || expanded.Count == 0)
			return;
		foreach (var (oldSide, blobLine) in expanded)
		{
			int? docLine = oldSide ? model.DocLineFromOldLine(blobLine) : model.DocLineFromNewLine(blobLine);
			if (docLine is not { } dl || dl < 1 || dl > Editor.Document.LineCount)
				continue;
			int offset = Editor.Document.GetLineByNumber(dl).Offset;
			foreach (var folding in foldingManager.AllFoldings.Where(f => f.StartOffset == offset))
				folding.IsFolded = false;
		}
	}

	/// <summary>
	/// What each gap still hides, as blob positions. Splicing a comment thread into the
	/// document renumbers every line below it, so how far the reader has opened the context
	/// has to be carried by content rather than by line number - the same reason fold state
	/// is.
	/// </summary>
	List<((bool OldSide, int Line) First, (bool OldSide, int Line) Last)> CaptureGaps()
	{
		var carried = new List<((bool, int), (bool, int))>();
		if (contextGaps is null || model is null)
			return carried;
		foreach (var gap in contextGaps.Gaps)
		{
			if (BlobPosition(gap.FirstLine) is { } first && BlobPosition(gap.LastLine) is { } last)
				carried.Add((first, last));
		}
		return carried;

		(bool OldSide, int Line)? BlobPosition(int docLine)
		{
			if (docLine < 1 || docLine > model.Tags.Count)
				return null;
			var tag = model.Tags[docLine - 1];
			return tag.NewLine > 0 ? (false, tag.NewLine) : tag.OldLine > 0 ? (true, tag.OldLine) : null;
		}
	}

	void RestoreGaps(
		List<((bool OldSide, int Line) First, (bool OldSide, int Line) Last)> carried, DiffDocumentModel m)
	{
		if (contextGaps is null || carried.Count == 0)
			return;
		var gaps = new List<ContextGap>();
		foreach (var (first, last) in carried)
		{
			if (DocLine(first) is { } firstDoc && DocLine(last) is { } lastDoc && lastDoc >= firstDoc)
				gaps.Add(new ContextGap(firstDoc, lastDoc));
		}
		contextGaps.Restore(gaps);

		int? DocLine((bool OldSide, int Line) position)
			=> position.OldSide ? m.DocLineFromOldLine(position.Line) : m.DocLineFromNewLine(position.Line);
	}

	void ApplyModelToEditor(DiffDocumentModel m)
	{
		Editor.Text = m.Text;
		margin.Tags = m.Tags;
		margin.InvalidateMeasure();
		Overview.Attach(Editor, m.Tags);
		InstallFoldsAndGaps(m);
		ApplyMarginCursors();
		referenceGenerator.References = null;
		markers.RemoveAll(_ => true);
		QueueSemanticsRefresh();
	}


	void OnCommentBoxKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
		{
			e.Handled = true;
			OnCommentSave(sender, e);
		}
		else if (e.Key == Key.Escape)
		{
			e.Handled = true;
			OnCommentCancel(sender, e);
		}
	}

	/// <summary>
	/// An empty editor may be dismissed by clicking away from it - there is nothing to lose,
	/// and a stray click should not need a button. Once something has been written, only Save
	/// draft, Cancel or Esc close it: a click aimed at the code behind it would otherwise take
	/// the words with it.
	/// </summary>
	void OnCommentTextChanged(object? sender, TextChangedEventArgs e)
		=> CommentPopup.IsLightDismissEnabled = string.IsNullOrEmpty(CommentBox.Text);

	void OnCommentSave(object? sender, RoutedEventArgs e) => SaveInlineCommentAsync().HandleExceptions();

	async Task SaveInlineCommentAsync()
	{
		if (inlineCommentTarget is not { } target || App.Workspace is not { } ws)
			return;
		string body = CommentBox.Text?.Trim() ?? "";
		if (body.Length == 0)
			return;
		if (editingDraftId is { } editing)
		{
			ws.Comments.UpdateDraft(editing, body);
			editingDraftId = null;
		}
		else
		{
			ws.Comments.BeginComment(target, activatePane: false);
			await ws.Comments.CommitDraftAsync(body);
		}
		CommentBox.Text = "";
		CommentPopup.IsOpen = false;
		inlineCommentTarget = null;
		Editor.TextArea.Focus();
	}

	void OnCommentCancel(object? sender, RoutedEventArgs e)
	{
		editingDraftId = null;
		CommentBox.Text = "";
		CommentPopup.IsOpen = false;
		inlineCommentTarget = null;
		Editor.TextArea.Focus();
	}

	public void ToggleBlameCommand() => ToggleBlameAsync().HandleExceptions();

	/// <summary>Whether the blame margin is showing, so a menu can say which state its toggle
	/// is in rather than only offering to flip it.</summary>
	public bool BlameVisible => blameVisible;

	/// <summary>Where the selected text came from: every commit that added or removed it.</summary>
	public void HistoryOfSelectionCommand()
	{
		string text = Editor.SelectedText;
		if (viewModel is null || string.IsNullOrWhiteSpace(text))
			return;
		App.Workspace?.RequestPickaxe(text, viewModel.File.Path);
	}

	/// <summary>Opens the line under the caret in VS Code, on the side it belongs to, so a
	/// breakpoint can be set where the change is being read.</summary>
	public void DebugHereCommand()
	{
		if (CaretBlobPosition() is not { } pos)
			return;
		App.Workspace?.OpenInVsCodeAsync(pos.OldSide, pos.RelPath, pos.Line).HandleExceptions();
	}
	public void GoToDefinitionCommand() => NavigateToDefinitionAtCaret();
	public void FindReferencesCommand() => ShowReferencesAtCaret();
	public void HighlightOccurrencesCommand() => HighlightOccurrencesAtCaretAsync().HandleExceptions();

	/// <summary>The semantic commands are offered only once there is a compilation to ask;
	/// before that they would answer as if the code had no definitions or callers.</summary>
	void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		bool ready = App.Workspace is { SemanticsReady: true };
		CtxGoToDefinition.IsEnabled = ready;
		CtxFindReferences.IsEnabled = ready;
		CtxHighlightOccurrences.IsEnabled = ready;
		CtxCallGraph.IsEnabled = ready;
	}

	void OnCtxGoToDefinition(object? s, RoutedEventArgs e) => GoToDefinitionCommand();
	void OnCtxFindReferences(object? s, RoutedEventArgs e) => FindReferencesCommand();
	/// <summary>Places the caret and highlights occurrences there, for driving checks.</summary>
	public void HighlightAtCommand(int line, int column)
	{
		if (line > 0)
			Editor.TextArea.Caret.Line = line;
		Editor.TextArea.Caret.Column = column;
		HighlightOccurrencesAtCaretAsync().HandleExceptions();
	}

	void OnCtxHighlightOccurrences(object? s, RoutedEventArgs e) => HighlightOccurrencesCommand();
	void OnCtxNextHunk(object? s, RoutedEventArgs e) => JumpToHunk(1);
	void OnCtxPrevHunk(object? s, RoutedEventArgs e) => JumpToHunk(-1);
	void OnCtxToggleBlame(object? s, RoutedEventArgs e) => ToggleBlameCommand();
	void OnCtxComment(object? s, RoutedEventArgs e) => CommentAtCaretCommand();

	void OnCtxNextUncovered(object? s, RoutedEventArgs e) => JumpToNextUncovered();

	void OnCtxNextCommit(object? s, RoutedEventArgs e)
		=> App.Workspace?.Scopes.StepCommitAsync(1).HandleExceptions();

	void OnCtxPrevCommit(object? s, RoutedEventArgs e)
		=> App.Workspace?.Scopes.StepCommitAsync(-1).HandleExceptions();

	void OnCtxHistoryOfSelection(object? s, RoutedEventArgs e) => HistoryOfSelectionCommand();
	void OnCtxCopy(object? s, RoutedEventArgs e) => Editor.Copy();

	void OnCtxCallGraph(object? s, RoutedEventArgs e) => ShowCallGraphCommand();

	public void ShowCallGraphCommand()
	{
		if (CaretBlobPosition() is { } pos)
		{
			App.Workspace?.Factory?.ShowPane("CallGraph");
			App.Workspace?.RequestCallGraphAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide).HandleExceptions();
		}
	}

	/// <summary>Opens VS Code on the worktree of the caret's side (base for removed lines,
	/// head otherwise) at the caret position, for stepping through the reviewed revision
	/// with a real debugger.</summary>
	void OnCtxDebugInVsCode(object? s, RoutedEventArgs e) => DebugHereCommand();

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

	void OnCoverageChanged()
	{
		Dispatcher.UIThread.Post(() => {
			var hits = viewModel is not null
				? App.Workspace?.Coverage?.GetValueOrDefault(viewModel.File.Path)
				: null;
			coverageMargin.Tags = model?.Tags;
			coverageMargin.HitsByNewLine = hits;
			bool wanted = hits is not null && viewModel is not { Historical: true };
			if (wanted && !coverageMarginVisible)
				Editor.TextArea.LeftMargins.Insert(0, coverageMargin);
			else if (!wanted && coverageMarginVisible)
				Editor.TextArea.LeftMargins.Remove(coverageMargin);
			coverageMarginVisible = wanted;
			coverageMargin.InvalidateVisual();
		});
	}

	void OnThemeChangedForSemantics(object? sender, EventArgs e) => QueueSemanticsRefresh();

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (viewModel is not null)
			viewModel.CaretRequested -= OnCaretRequested;
		if (DataContext is not DiffDocumentViewModel vm)
			return;
		viewModel = vm;
		ReviewViews.Register(this);
		viewsByDocument.AddOrUpdate(vm, this);
		vm.CaretRequested += OnCaretRequested;
		model = vm.Model;
		// One side's text, not the document's: the unified diff interleaves the two, and no
		// format parses as itself with the lines it used to have spliced back into it.
		Editor.SyntaxHighlighting = HighlightingService.GetForFile(
			vm.File.Path,
			() => vm.Model.GetSideText(oldSide: vm.File.Kind == Core.Diff.FileChangeKind.Deleted).Text);
		Editor.Text = vm.Model.Text;
		// A source view is one blob shown whole - a file opened from the Explorer, a decompiled
		// type, the base side of a file the change does not touch. There is no other side for
		// its lines to be numbered against, and two identical columns of numbers only read as a
		// diff that is missing.
		margin.Columns = vm.IsSourceView ? DiffLineNumberColumns.New : DiffLineNumberColumns.Both;
		margin.Tags = vm.Model.Tags;
		margin.InvalidateMeasure();
		Overview.Attach(Editor, vm.Model.Tags);
		InstallFoldsAndGaps(vm.Model);
		ApplyMarginCursors();
		referenceGenerator.References = null;
		markers.RemoveAll(_ => true);
		QueueSemanticsRefresh();
		OnCommentsChangedForThreads();
		// Queued behind the thread rebuild posted just above, so the mapping sees the
		// document the reader will actually be looking at.
		if (vm.TakePendingCaret() is { } pending)
			Dispatcher.UIThread.Post(() => MoveCaretToBlobLine(pending.Line, pending.OldSide));
		if (ActiveView == this)
			ActiveViewChanged?.Invoke();
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
		if (model is null || viewModel is null || viewModel.Historical || App.Workspace is not { } ws)
			return;
		var m = model;
		var vm = viewModel;

		var headSem = ws.SemanticsFor(oldSide: false);
		var baseSem = ws.SemanticsFor(oldSide: true);
		var headTokens = await TokensForSideAsync(headSem, vm.File.Path, m, oldSide: false);
		bool hasRemoved = m.Tags.Any(t => t.Kind == DiffLineKind.Removed);
		var baseTokens = hasRemoved
			? await TokensForSideAsync(baseSem, vm.File.OldPath, m, oldSide: true)
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

	/// <summary>
	/// Tokens for one side of the diff. Token positions are offsets into the text they
	/// were computed from, so the loaded workspace's are only usable when it holds the
	/// revision on screen - which it does not when a single commit of a file is being
	/// read and later commits change it. The displayed text is then classified on its
	/// own: less knowledgeable, but aligned with what is actually shown.
	/// </summary>
	static async Task<IReadOnlyList<SemanticToken>> TokensForSideAsync(
		RoslynWorkspaceService? semantics, string relPath, DiffDocumentModel model, bool oldSide)
	{
		var (displayed, _) = model.GetSideText(oldSide);
		if (displayed.Length == 0)
			return [];
		if (semantics is { State: SemanticState.Ready or SemanticState.SyntaxOnly }
			&& await semantics.GetDocumentTextAsync(relPath, CancellationToken.None) is { } loaded
			&& Same(loaded, displayed))
		{
			return await semantics.GetSemanticTokensAsync(relPath, CancellationToken.None);
		}
		return semantics is { State: SemanticState.Ready or SemanticState.SyntaxOnly }
			? await semantics.GetSemanticTokensForTextAsync(relPath, displayed, CancellationToken.None)
			: [];

		static bool Same(string a, string b) => string.Equals(
			a.ReplaceLineEndings("\n").TrimEnd('\n'),
			b.ReplaceLineEndings("\n").TrimEnd('\n'),
			StringComparison.Ordinal);
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

	void OnCaretRequested(int blobLine, bool oldSide)
	{
		Dispatcher.UIThread.Post(() => MoveCaretToBlobLine(blobLine, oldSide));
	}

	/// <summary>
	/// Moves the caret to a line of the file. The mapping happens here rather than where the
	/// request came from: threads are spliced into the document after it opens, and a document
	/// line worked out before that describes a different place afterwards.
	/// </summary>
	void MoveCaretToBlobLine(int blobLine, bool oldSide)
	{
		int? docLine = oldSide ? model?.DocLineFromOldLine(blobLine) : model?.DocLineFromNewLine(blobLine);
		MoveCaretToLine(docLine ?? blobLine);
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
		// A line hidden as context has to be given back before the caret can sit on it.
		contextGaps?.Reveal(line);
		Editor.TextArea.Caret.Line = line;
		Editor.TextArea.Caret.Column = 1;
		Editor.TextArea.Focus();
		ScrollToLineWhenLaidOut(line);
	}

	int? pendingScrollLine;

	/// <summary>
	/// Scrolls to a line once the editor is able to reach it. A document being opened has no
	/// scroll viewer until its template is applied, and one that has just been given its text
	/// has an extent measured from estimated line heights, so a scroll issued now does nothing
	/// or stops short of the line. Repeating it after the next layout pass, when the lines
	/// above have real heights, lands on it.
	/// </summary>
	void ScrollToLineWhenLaidOut(int line)
	{
		Editor.ScrollToLine(line);
		pendingScrollLine = line;
		Editor.LayoutUpdated -= ScrollAfterLayout;
		Editor.LayoutUpdated += ScrollAfterLayout;
	}

	void ScrollAfterLayout(object? sender, EventArgs e)
	{
		Editor.LayoutUpdated -= ScrollAfterLayout;
		if (pendingScrollLine is not { } line)
			return;
		pendingScrollLine = null;
		Editor.ScrollToLine(line);
	}

	/// <summary>
	/// Folds are the code's structure only - types, members, #regions. Unchanged context is
	/// hidden by <see cref="contextGaps"/> instead, which is why expanding a method no longer
	/// reveals context and collapsing everything no longer swallows the change.
	/// </summary>
	void InstallFoldsAndGaps(DiffDocumentModel m)
	{
		foldingManager ??= FoldingManager.Install(Editor.TextArea);
		if (contextGaps is not null)
			ContextGapFoldingMargin.Install(Editor.TextArea, foldingManager, contextGaps.HasBar);
		structuralRanges = [];
		if (viewModel is { } vm && vm.File.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
		{
			bool oldSide = vm.File.Kind == Core.Diff.FileChangeKind.Deleted;
			var (sideText, sideToDocLine) = m.GetSideText(oldSide);
			structuralRanges.AddRange(DiffFolding.Members(sideText, sideToDocLine));
		}
		// The same ranges the folds use say where each declaration begins and ends, which is
		// what the gaps need to cut a run around the header of whatever a change is inside. A
		// patch gets none: it is already only what git chose to print, and the runs a gap would
		// swallow are the commit message and the per-file headers that say what follows them.
		bool gaps = m.Hunks.Count > 0 && viewModel is not { IsPatch: true };
		contextGaps?.Install(m.Tags, gaps, structuralRanges);
		RefreshFoldings();
	}

	/// <summary>
	/// Installs the structural folds that apply to what is actually shown. A fold beginning
	/// inside hidden context is left out: the gap's control stands for all those lines at
	/// once, so the margin would draw that fold's marker beside the control and offer to
	/// collapse code the reader cannot see. They come back as the context does.
	/// </summary>
	void RefreshFoldings()
	{
		if (foldingManager is null)
			return;
		var shown = contextGaps?.ClipToVisible(structuralRanges) ?? structuralRanges;
		foldingManager.Clear();
		foldingManager.UpdateFoldings(FoldInstaller.ToFoldings(Editor.Document, shown), -1);
	}

	void OnEditorKeyDown(object? sender, KeyEventArgs e)
	{
		// The search panel is a child of the text area, so what is typed into its box tunnels
		// through here on the way down. A review gesture is a letter to anyone typing one:
		// leave every keystroke aimed at a text box alone.
		if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
			return;
		// Escape is this layout's own: it clears the markers only it draws, and it is not a
		// review gesture - anything else listening for it should still hear it.
		if (e is { Key: Key.Escape, KeyModifiers: KeyModifiers.None })
		{
			markers.RemoveAll(_ => true);
			return;
		}
		e.Handled = ReviewGestures.Handle(e, this);
	}

	// Where the last left-button press happened and which modifiers were held for it; null
	// while no press is in flight. The modifiers belong to the press: a word double-clicked
	// and then held while Ctrl goes down would otherwise navigate on release, having been
	// asked only to select.
	(Avalonia.Point Position, KeyModifiers Modifiers)? clickStart;

	// WPF's default minimum drag distance; a release farther than this is a drag.
	const double MinimumDragDistance = 4;

	void OnTextAreaPointerPressedForClick(object? sender, PointerPressedEventArgs e)
	{
		clickStart = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
			? (e.GetPosition(this), e.KeyModifiers)
			: null;
	}

	void OnTextViewPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		var start = clickStart;
		clickStart = null;
		if (e.InitialPressMouseButton != MouseButton.Left || start is null)
			return;
		var delta = e.GetPosition(this) - start.Value.Position;
		if (Math.Abs(delta.X) >= MinimumDragDistance || Math.Abs(delta.Y) >= MinimumDragDistance)
			return;
		// A stationary click has already placed the caret. Ctrl+Click navigates; a plain
		// click on a symbol highlights its occurrences in this document.
		if (start.Value.Modifiers == KeyModifiers.Control)
		{
			Editor.TextArea.ClearSelection();
			NavigateToDefinitionAtCaret();
		}
		else if (start.Value.Modifiers == KeyModifiers.None && Editor.TextArea.Selection.IsEmpty)
		{
			HighlightOccurrencesAtCaretAsync().HandleExceptions();
		}
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
		if (viewModel is null or { Historical: true } || CaretBlobPosition() is not { } pos)
			return;
		var origin = new ReviewWorkspace.NavEntryOrigin(viewModel.Id, pos.Line, pos.OldSide);
		App.Workspace?.NavigateToDefinitionAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide, origin).HandleExceptions();
	}

	void ShowReferencesAtCaret()
	{
		if (viewModel is { Historical: true } || CaretBlobPosition() is not { } pos)
			return;
		App.Workspace?.ShowReferencesAtAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide).HandleExceptions();
	}

	async Task HighlightOccurrencesAtCaretAsync()
	{
		markers.RemoveAll(_ => true);
		if (viewModel is { Historical: true } || CaretBlobPosition() is not { } pos || model is null || App.Workspace is not { } ws)
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

	void JumpToNextUncovered()
	{
		if (model is null || viewModel is null || App.Workspace is not { } ws)
			return;
		int start = Editor.TextArea.Caret.Line;
		for (int line = start + 1; line <= model.Tags.Count; line++)
		{
			var tag = model.Tags[line - 1];
			if (tag.NewLine > 0 && ws.IsUncoveredAdded(viewModel.File.Path, tag.NewLine))
			{
				MoveCaretToLine(line);
				return;
			}
		}
	}

	/// <summary>Moves to the next hunk in that direction, and says whether there was one.</summary>
	bool JumpToHunk(int direction)
	{
		if (model is null || model.Hunks.Count == 0)
			return false;
		int caretLine = Editor.TextArea.Caret.Line;
		HunkSpan? target = direction > 0
			? model.Hunks.Cast<HunkSpan?>().FirstOrDefault(h => h!.Value.FirstDocLine > caretLine)
			: model.Hunks.Cast<HunkSpan?>().LastOrDefault(h => h!.Value.FirstDocLine < caretLine);
		if (target is null)
			return false;
		MoveCaretToLine(target.Value.FirstDocLine);
		return true;
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
		string newRev = vm.Historical ? vm.HistoricalSha! : ws.HeadSha;
		string? oldRev = vm.Historical ? vm.HistoricalSha + "^" : ws.BaseSha;
		try
		{
			if (vm.File.Kind != FileChangeKind.Deleted)
				newBlame = await ws.Git.BlameAsync(newRev, vm.File.Path);
			if (oldRev is not null && m.Tags.Any(t => t.Kind == DiffLineKind.Removed))
				oldBlame = await ws.Git.BlameAsync(oldRev, vm.File.OldPath);
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
		UpdateTextCursor(e);
		ToolTip.SetIsOpen(Editor, false);
		hoverTimer.Stop();
		hoverTimer.Start();
	}

	bool foldCursorActive;

	void UpdateTextCursor(PointerEventArgs e)
		=> foldCursorActive = FoldCursor.Update(Editor.TextArea.TextView, e, foldCursorActive);

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

	/// <summary>Quick info under the pointer. Every way of coming up empty is logged, once per
	/// reason, so that "no tooltip" can be told apart from "no hover".</summary>
	async Task ShowHoverAsync()
	{
		if (model is null || viewModel is null || App.Workspace is not { } ws)
			return;
		if (viewModel.Historical)
		{
			HoverLog("hover: historical document - semantics are off here");
			return;
		}
		var position = Editor.GetPositionFromPoint(lastPointerPosition);
		if (position is null)
		{
			HoverLog("hover: pointer is past the end of the text");
			return;
		}
		int docLine = position.Value.Line;
		if (docLine < 1 || docLine > model.Tags.Count)
		{
			HoverLog($"hover: row {docLine} is outside the {model.Tags.Count} this document has");
			return;
		}
		var tag = model.Tags[docLine - 1];
		bool oldSide = tag.NewLine == 0;
		if (oldSide && tag.OldLine == 0)
		{
			HoverLog($"hover: row {docLine} belongs to neither side");
			return;
		}
		string relPath = oldSide ? viewModel.File.OldPath : viewModel.File.Path;
		int blobLine = oldSide ? tag.OldLine : tag.NewLine;
		var sem = ws.SemanticsFor(oldSide);
		if (sem is not { State: SemanticState.Ready or SemanticState.SyntaxOnly })
		{
			HoverLog($"hover: semantics are {sem?.State.ToString() ?? "not loaded"}");
			return;
		}
		int? pos = await sem.GetPositionAsync(relPath, blobLine, position.Value.Column, CancellationToken.None);
		if (pos is null)
		{
			HoverLog($"hover: {relPath}:{blobLine} is not a line the compilation has");
			return;
		}
		string? text = await sem.GetQuickInfoAsync(relPath, pos.Value, CancellationToken.None);
		if (string.IsNullOrEmpty(text))
		{
			HoverLog($"hover: nothing to say about {relPath}:{blobLine},{position.Value.Column}");
			return;
		}
		HoverLog($"hover: {relPath}:{blobLine},{position.Value.Column} -> {text.Split('\n')[0]}");
		ToolTip.SetTip(Editor, text);
		ToolTip.SetIsOpen(Editor, true);
	}

	string? lastHoverLog;

	/// <summary>Logs a hover outcome, skipping a repeat of the one before it: a pointer at rest
	/// asks again every time it twitches, and the log is for reading.</summary>
	void HoverLog(string message)
	{
		if (message == lastHoverLog)
			return;
		lastHoverLog = message;
		Core.Infra.CliLog.Write("semantics", message);
	}

	#endregion
}
