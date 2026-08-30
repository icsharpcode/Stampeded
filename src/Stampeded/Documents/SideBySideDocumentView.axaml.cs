using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

using AvaloniaEdit.Folding;
using AvaloniaEdit.Search;

using Stampeded.Core.Diff;
using Stampeded.Diff;
using Stampeded.Editor;

namespace Stampeded.Documents;

/// <summary>
/// Two synchronized editors fed from one alignment; equal line counts (Filler rows on
/// the shorter side) make scroll sync a plain offset copy with a re-entrancy guard.
/// </summary>
public partial class SideBySideDocumentView : UserControl, IReviewDocumentView
{
	/// <summary>What this layout carries out today. The rest are declared by the interface and
	/// answered here with a status line rather than silence, so the difference between the two
	/// layouts is written down in one place instead of being discovered by pressing a key.
	/// </summary>
	public ReviewCommands Supported => ReviewCommands.JumpToHunk | ReviewCommands.GoToDefinition
		| ReviewCommands.FindReferences | ReviewCommands.CommentAtCaret;

	public string DocumentId => (DataContext as SideBySideDocumentViewModel)?.Id ?? "";

	/// <summary>The pane the caret is in, which is the one a command means; the left one until
	/// the reader has been in either.</summary>
	SideBySidePane? FocusedPane
		=> Right.TextArea.IsFocused || Right.TextArea.IsKeyboardFocusWithin ? rightPane : leftPane;

	ReviewTextEditor FocusedEditor
		=> Right.TextArea.IsFocused || Right.TextArea.IsKeyboardFocusWithin ? Right : Left;

	public bool BlameVisible => false;

	/// <summary>Moves the caret to the next row that is part of the change, in the pane the
	/// reader is in. The tags say which rows those are, and a run of them is one hunk.</summary>
	public bool JumpToHunkCommand(int direction)
	{
		var tags = FocusedEditor == Right ? rightTags : leftTags;
		if (tags is null || tags.Count == 0)
			return false;
		var editor = FocusedEditor;
		int line = editor.TextArea.Caret.Line;
		// Off the current run first, so stepping does not stop on the row it started in.
		int next = line;
		while (InHunk(tags, next) && next + direction >= 1 && next + direction <= tags.Count)
			next += direction;
		while (next + direction >= 1 && next + direction <= tags.Count && !InHunk(tags, next))
			next += direction;
		if (!InHunk(tags, next))
			return false;
		MoveCaretTo(editor, next);
		return true;
	}

	public void JumpToEdgeHunk(int direction)
	{
		var tags = FocusedEditor == Right ? rightTags : leftTags;
		if (tags is null || tags.Count == 0)
			return;
		var rows = Enumerable.Range(1, tags.Count).Where(row => InHunk(tags, row));
		if ((direction > 0 ? rows.FirstOrDefault() : rows.LastOrDefault()) is > 0 and var row)
			MoveCaretTo(FocusedEditor, row);
	}

	/// <summary>Whether a row is part of the change rather than context or filler.</summary>
	static bool InHunk(IReadOnlyList<DiffLineTag> tags, int docLine)
		=> docLine >= 1 && docLine <= tags.Count
			&& tags[docLine - 1].Kind is not (DiffLineKind.Context or DiffLineKind.Filler
				or DiffLineKind.Comment);

	static void MoveCaretTo(ReviewTextEditor editor, int docLine)
	{
		editor.TextArea.Caret.Line = docLine;
		editor.TextArea.Caret.Column = 1;
		editor.ScrollToLine(docLine);
	}

	public (int BlobLine, bool OldSide)? CaretOrigin
		=> FocusedPane?.CaretBlobPosition() is { } pos ? (pos.Line, pos.OldSide) : null;

	public void GoToDefinitionCommand() => FocusedPane?.NavigateToDefinition();

	public void FindReferencesCommand() => FocusedPane?.ShowReferences();

	public void JumpToUncoveredCommand() => NotHere("Coverage");

	public void ToggleBlameCommand() => NotHere("Blame");

	/// <summary>Height the comment editor needs, as laid out in the view's markup.</summary>
	const double CommentBoxHeight = 150;

	CommentTarget? inlineCommentTarget;

	/// <summary>The draft the editor is rewriting, when it was opened on one.</summary>
	Guid? editingDraftId;

	public void CommentAtCaretCommand() => CommentAtCaret(null);

	/// <summary>
	/// Opens the comment editor under a row of the pane the caret is in. The row is the
	/// caret's own unless a thread is being answered, in which case it is the bottom of what
	/// has already been said there - a reply written on top of the thread it answers is a
	/// reply nobody can read while writing it.
	/// </summary>
	void CommentAtCaret(int? anchorRow, long inReplyTo = 0)
	{
		if (FocusedPane?.CaretBlobPosition() is not { } position)
			return;
		if (App.Workspace is { Comments.CanComment: false } local)
		{
			local.PostStatus("Comments need a pull request; this is a local review.");
			return;
		}
		var editor = FocusedEditor;
		editingDraftId = null;
		CommentBox.Text = "";
		var docLine = editor.Document.GetLineByNumber(editor.TextArea.Caret.Line);
		string text = editor.Document.GetText(docLine.Offset, docLine.Length);
		inlineCommentTarget = new CommentTarget(
			position.RelPath, position.OldSide, position.Line, text, inReplyTo == 0 ? null : inReplyTo);
		CommentTargetText.Text = (inReplyTo == 0 ? "" : "Reply  |  ")
			+ $"{position.RelPath}:{position.Line}{(position.OldSide ? " (base)" : "")}  |  {text.Trim()}";
		int anchorAt = Math.Clamp(anchorRow ?? editor.TextArea.Caret.Line, 1, editor.Document.LineCount);
		var view = editor.TextArea.TextView;
		double anchorY = (view.GetVisualPosition(
			new AvaloniaEdit.TextViewPosition(anchorAt, 1), AvaloniaEdit.Rendering.VisualYPosition.LineBottom)
			- view.ScrollOffset).Y;
		// One editor over both panes, anchored to the left one; a comment on the right side is
		// pushed across by where that pane starts, so the box sits under the line it names.
		double paneOffset = editor == Right ? Right.Bounds.X - Left.Bounds.X : 0;
		double marginsWidth = editor.TextArea.LeftMargins.OfType<Avalonia.Controls.Control>().Sum(m => m.Bounds.Width);
		CommentPopup.HorizontalOffset = paneOffset + marginsWidth + 8;
		CommentPopup.VerticalOffset = Math.Max(0, Math.Min(anchorY, view.Bounds.Height - CommentBoxHeight));
		CommentPopup.IsLightDismissEnabled = true;
		CommentPopup.IsOpen = true;
		CommentBox.Focus();
	}

	/// <summary>Opens the editor on a draft that already exists. Saving rewrites that draft
	/// rather than adding another: it is the same remark, said better.</summary>
	void EditDraft(Guid draftId, string body, ThreadData thread)
	{
		if (!MoveToThread(thread))
			return;
		CommentAtCaret(LastThreadRowAfter(thread));
		if (!CommentPopup.IsOpen)
			return;
		editingDraftId = draftId;
		CommentBox.Text = body;
		CommentBox.CaretIndex = body.Length;
	}

	void ReplyInThread(ThreadData thread, long replyTo)
	{
		if (MoveToThread(thread))
			CommentAtCaret(LastThreadRowAfter(thread), replyTo);
	}

	/// <summary>Puts the caret on the line a thread hangs on, in the pane that shows that
	/// side, so what follows is written about the right blob.</summary>
	bool MoveToThread(ThreadData thread)
	{
		var pane = thread.OldSide ? leftPane : rightPane;
		if (pane?.DocLineFromBlobLine(thread.BlobLine) is not { } docLine)
			return false;
		Reveal(docLine);
		pane.MoveCaretToBlobLine(thread.BlobLine);
		(thread.OldSide ? Left : Right).TextArea.Focus();
		return true;
	}

	/// <summary>The last of the rows reserved for threads under the line a thread hangs on.
	/// Everything said there is spliced in below the code, so this is the bottom of it.</summary>
	int LastThreadRowAfter(ThreadData thread)
	{
		var tags = thread.OldSide ? leftTags : rightTags;
		if (tags is null || viewModel?.Pair.DocLineFor(thread.OldSide, thread.BlobLine) is not { } row)
			return 1;
		int last = row;
		for (int line = row + 1; line <= tags.Count && tags[line - 1].Kind == DiffLineKind.Comment; line++)
			last = line;
		return last;
	}

	void OnCommentTextChanged(object? sender, TextChangedEventArgs e)
		=> CommentPopup.IsLightDismissEnabled = string.IsNullOrEmpty(CommentBox.Text);

	void OnCommentSuggest(object? sender, RoutedEventArgs e) => Suggestion.Prefill(CommentBox, inlineCommentTarget);

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
		CloseCommentEditor();
	}

	void OnCommentCancel(object? sender, RoutedEventArgs e) => CloseCommentEditor();

	void CloseCommentEditor()
	{
		editingDraftId = null;
		CommentBox.Text = "";
		CommentPopup.IsOpen = false;
		inlineCommentTarget = null;
		FocusedEditor.TextArea.Focus();
	}

	public void HighlightOccurrencesCommand() => NotHere("Highlighting occurrences");

	public void ShowCallGraphCommand() => NotHere("The call graph");

	public void HistoryOfSelectionCommand() => NotHere("History of a selection");

	public void DebugHereCommand() => NotHere("Debug here");

	/// <summary>Records which command the side-by-side layout has not got yet. The menu greys
	/// these out, so this is for the key presses that reach them anyway: the Log pane is where
	/// it lands, since a diff in front has no status line of its own.</summary>
	static void NotHere(string what)
	{
		string message = $"{what} is not available in the side-by-side layout yet; the unified one has it.";
		Core.Infra.CliLog.Write("action", message);
		App.Workspace?.PostStatus(message);
	}

	readonly DiffLineNumberMargin leftMargin = new();
	readonly DiffLineNumberMargin rightMargin = new();
	readonly Editor.ThreadElementGenerator leftThreadGenerator = new();
	readonly Editor.ThreadElementGenerator rightThreadGenerator = new();
	CommentThreadBox? leftBoxes;
	CommentThreadBox? rightBoxes;
	Dictionary<string, ThreadData>? threadsByKey;
	/// <summary>One per thread row, so the pane that draws nothing there draws it exactly as
	/// tall as the box on the other side.</summary>
	readonly Dictionary<string, ThreadRowHeight> rowHeights = [];
	SideBySidePane? leftPane;
	SideBySidePane? rightPane;
	SideBySideDocumentViewModel? viewModel;
	IReadOnlyList<DiffLineTag>? leftTags;
	IReadOnlyList<DiffLineTag>? rightTags;
	FoldingManager? leftFolding;
	FoldingManager? rightFolding;
	ContextGapView? contextGaps;
	List<FoldRange> structuralRanges = [];
	bool syncingFolds;
	bool syncing;
	bool scrollWired;
	int wireAttempts;
	Stampeded.Editor.SyntaxPainter? painter;
	Stampeded.Editor.SlicedPaint? leftPaint;
	Stampeded.Editor.SlicedPaint? rightPaint;

	AvaloniaEdit.Highlighting.RichTextColorizer? leftColorizer;
	AvaloniaEdit.Highlighting.RichTextColorizer? rightColorizer;

	public SideBySideDocumentView()
	{
		InitializeComponent();
		SearchPanel.Install(Left);
		SearchPanel.Install(Right);
		Left.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => leftTags));
		Right.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => rightTags));
		leftMargin.Columns = DiffLineNumberColumns.Old;
		rightMargin.Columns = DiffLineNumberColumns.New;
		Left.TextArea.LeftMargins.Insert(0, leftMargin);
		Right.TextArea.LeftMargins.Insert(0, rightMargin);
		Left.TextArea.TextView.VisualLinesChanged += (_, _) => MirrorFolds(leftFolding, rightFolding);
		Right.TextArea.TextView.VisualLinesChanged += (_, _) => MirrorFolds(rightFolding, leftFolding);
		leftPane = new SideBySidePane(Left, oldSide: true);
		rightPane = new SideBySidePane(Right, oldSide: false);
		// On the way down to the editors, for the same reason the unified layout does it: some
		// of these keys are bindings of AvaloniaEdit's own, and a key it has acted on never
		// reaches the window that would otherwise answer for them.
		Left.TextArea.AddHandler(KeyDownEvent, OnPaneKeyDown, RoutingStrategies.Tunnel);
		Right.TextArea.AddHandler(KeyDownEvent, OnPaneKeyDown, RoutingStrategies.Tunnel);
		FoldViewportAnchor.Install(Left);
		FoldViewportAnchor.Install(Right);
		CommentBox.AddHandler(KeyDownEvent, OnCommentBoxKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
		leftBoxes = new CommentThreadBox(Left.TextArea.TextView, EditDraft, ReplyInThread);
		rightBoxes = new CommentThreadBox(Right.TextArea.TextView, EditDraft, ReplyInThread);
		leftThreadGenerator.ControlFactory = key => ThreadControl(key, paneIsOldSide: true);
		rightThreadGenerator.ControlFactory = key => ThreadControl(key, paneIsOldSide: false);
		Left.TextArea.TextView.ElementGenerators.Add(leftThreadGenerator);
		Right.TextArea.TextView.ElementGenerators.Add(rightThreadGenerator);
	}

	/// <summary>
	/// What one pane draws on a thread's row: the box, when the comment is about the blob this
	/// pane shows, and otherwise a spacer that follows the box's height. Both panes reserve
	/// the row - they are scrolled by copying one offset to the other, which only holds while
	/// they hold the same rows.
	/// </summary>
	Avalonia.Controls.Control? ThreadControl(string key, bool paneIsOldSide)
	{
		if (threadsByKey is null || !threadsByKey.TryGetValue(key, out var thread))
			return null;
		if (!rowHeights.TryGetValue(key, out var row))
			rowHeights[key] = row = new ThreadRowHeight();
		// An outdated thread has no line on either side; it is pinned at the top of the file
		// and drawn on the right, which is the side a review is read on.
		bool ownedHere = thread.OldSide == paneIsOldSide;
		if (!ownedHere)
			return new ThreadSpacer(row, (paneIsOldSide ? Left : Right).TextArea.TextView);
		var box = (paneIsOldSide ? leftBoxes : rightBoxes)!.Build(key, thread);
		row.Track(box);
		return box;
	}

	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		ReviewViews.Register(this);
		Dispatcher.UIThread.Post(WireScrollSync, DispatcherPriority.Loaded);
		if (App.Workspace is { } ws)
		{
			ws.SemanticsChanged += OnSemanticsChanged;
			ws.Comments.Changed += OnCommentsChanged;
		}
		// The colours are built once per text and carry the theme they were built in, so a
		// theme changed while a file is open has to build them again.
		Themes.ThemeManager.Current.ThemeChanged += OnThemeChangedForColors;
		RefreshSemantics();
		RebuildThreads();
	}

	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		ReviewViews.Unregister(this);
		if (App.Workspace is { } ws)
		{
			ws.SemanticsChanged -= OnSemanticsChanged;
			ws.Comments.Changed -= OnCommentsChanged;
		}
		Themes.ThemeManager.Current.ThemeChanged -= OnThemeChangedForColors;
		base.OnDetachedFromVisualTree(e);
	}

	void OnPaneKeyDown(object? sender, KeyEventArgs e)
	{
		// The search panel lives inside the text area, so what is typed into it tunnels through
		// here: a review gesture is a letter to anyone typing one.
		if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
			return;
		e.Handled = ReviewGestures.Handle(e, this);
	}

	void OnCommentBoxKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
		{
			e.Handled = true;
			SaveInlineCommentAsync().HandleExceptions();
		}
		else if (e.Key == Key.Escape)
		{
			e.Handled = true;
			CloseCommentEditor();
		}
	}

	void OnSemanticsChanged() => Dispatcher.UIThread.Post(RefreshSemantics);

	void RefreshSemantics()
	{
		leftPane?.RefreshSemanticsAsync().HandleExceptions();
		rightPane?.RefreshSemanticsAsync().HandleExceptions();
	}

	// The editors' ScrollViewers only exist once their templates are applied; sync their
	// Offset properties directly - equal line counts on both sides make a plain copy exact.
	void WireScrollSync()
	{
		if (scrollWired)
			return;
		var leftScroll = Left.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
		var rightScroll = Right.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
		if (leftScroll is null || rightScroll is null)
		{
			if (wireAttempts++ < 20)
				Dispatcher.UIThread.Post(WireScrollSync, DispatcherPriority.Background);
			return;
		}
		scrollWired = true;
		leftScroll.ScrollChanged += (_, _) => Sync(leftScroll, rightScroll);
		rightScroll.ScrollChanged += (_, _) => Sync(rightScroll, leftScroll);
	}

	void Sync(ScrollViewer source, ScrollViewer target)
	{
		if (syncing || target.Offset == source.Offset)
			return;
		syncing = true;
		target.Offset = source.Offset;
		// The guard is dropped a turn later, not here. A pane whose widest line is narrower
		// clamps the offset it was given and reports the clamp as a scroll of its own, from
		// inside the layout pass that measured it; answering that echo drags the pane the
		// reader is scrolling, and the two can go on correcting each other until the layout
		// gives up. The echo comes before the next turn of the dispatcher, so it is let go.
		Dispatcher.UIThread.Post(() => syncing = false, DispatcherPriority.Input);
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (viewModel is not null)
			viewModel.CaretRequested -= OnCaretRequested;
		if (DataContext is not SideBySideDocumentViewModel vm)
			return;
		viewModel = vm;
		vm.CaretRequested += OnCaretRequested;
		ReviewViews.Register(this);
		// The side the file still has: a pane's own text carries the filler rows that keep the
		// two in step, and those are not part of any format.
		painter = Stampeded.Editor.SyntaxPainter.For(
			vm.File.Path,
			() => vm.Pair.GetSideText(oldSide: vm.File.Kind == FileChangeKind.Deleted).Text);
		rowHeights.Clear();
		ApplyPair(vm);
		RebuildThreads();
		// A caret asked for before this view had the document: navigation opens a document and
		// then says where to land, and the two need not happen in that order.
		if (vm.TakePendingCaret() is { } pending)
			OnCaretRequested(pending.Line, pending.OldSide);
	}

	/// <summary>Puts the pair on screen: both texts, the tags every margin and renderer reads,
	/// the folds and the gaps. Called for the document the tab was opened with and again
	/// whenever the comment rows spliced into it change.</summary>
	void ApplyPair(SideBySideDocumentViewModel vm)
	{
		Left.Text = vm.Pair.LeftText;
		Right.Text = vm.Pair.RightText;
		leftTags = vm.Pair.LeftTags;
		rightTags = vm.Pair.RightTags;
		leftMargin.Tags = vm.Pair.LeftTags;
		rightMargin.Tags = vm.Pair.RightTags;
		leftMargin.InvalidateMeasure();
		rightMargin.InvalidateMeasure();
		InstallFoldings(vm);
		leftPane?.SetDocument(vm);
		rightPane?.SetDocument(vm);
		ApplySyntaxColors();
		RefreshSemantics();
	}

	/// <summary>
	/// Installs the syntax colours as a colorizer over each pane's own text, rather than
	/// letting the editor highlight the document. The colours come from a TextMate grammar,
	/// which the editor cannot be handed as a highlighting definition - and the text on screen
	/// changes under a pane whenever a comment thread reserves a row, so the colours are built
	/// where the text is set.
	/// </summary>
	void ApplySyntaxColors()
	{
		Apply(Left, ref leftColorizer, ref leftPaint);
		Apply(Right, ref rightColorizer, ref rightPaint);

		void Apply(ReviewTextEditor editor, ref AvaloniaEdit.Highlighting.RichTextColorizer? colorizer,
			ref Stampeded.Editor.SlicedPaint? paint)
		{
			paint?.Cancel();
			paint = null;
			if (colorizer is not null)
				editor.TextArea.TextView.LineTransformers.Remove(colorizer);
			colorizer = null;
			if (painter is null)
				return;
			// The colours are handed to the pane before they have been worked out: the model
			// is read as the rows are drawn, so painting it further only needs a redraw.
			var colors = new AvaloniaEdit.Highlighting.RichTextModel();
			colorizer = new AvaloniaEdit.Highlighting.RichTextColorizer(colors);
			editor.TextArea.TextView.LineTransformers.Insert(0, colorizer);
			paint = Stampeded.Editor.SlicedPaint.Start(
				Stampeded.Editor.DiffSyntaxColors.Whole(painter, editor.Document, colors),
				editor.TextArea.TextView.Redraw);
		}
	}

	void OnThemeChangedForColors(object? sender, EventArgs e) => ApplySyntaxColors();

	void OnCommentsChanged() => Dispatcher.UIThread.Post(RebuildThreads);

	/// <summary>
	/// Re-splices the pair with one reserved row per comment thread of this file. The caret is
	/// put back by blob line rather than by document line: the rows it is counted in have just
	/// moved.
	/// </summary>
	void RebuildThreads()
	{
		if (viewModel is not { } vm || App.Workspace is not { } ws)
			return;
		var threads = CommentThreads.For(ws, vm.File);
		threadsByKey = threads.Count == 0 ? null : threads;
		var anchors = CommentThreads.Anchors(threads);
		var target = anchors.Count == 0 ? vm.PristinePair : vm.PristinePair.WithThreadLines(anchors);
		if (target.LeftText == vm.Pair.LeftText && target.RightText == vm.Pair.RightText)
		{
			// The rows are already right; only what is drawn in them changed.
			Left.TextArea.TextView.Redraw();
			Right.TextArea.TextView.Redraw();
			return;
		}
		var caret = FocusedPane?.CaretBlobPosition();
		vm.ReplacePair(target);
		ApplyPair(vm);
		if (caret is { } position)
			OnCaretRequested(position.Line, position.OldSide);
	}

	/// <summary>
	/// Lands on a line of one blob. The side asked for gets the caret; when that side has no
	/// such line - a line added on the right has none on the left - the other pane takes it, so
	/// the answer is a row on screen rather than nothing at all. Queued behind layout: the
	/// panes may still be building their text when navigation arrives.
	/// </summary>
	void OnCaretRequested(int blobLine, bool oldSide)
		=> Dispatcher.UIThread.Post(
			() => {
				var (first, second) = oldSide ? (leftPane, rightPane) : (rightPane, leftPane);
				var pane = first?.DocLineFromBlobLine(blobLine) is not null ? first : second;
				if (pane?.DocLineFromBlobLine(blobLine) is { } docLine)
				{
					Reveal(docLine);
					pane.MoveCaretToBlobLine(blobLine);
					ScrollBothWhenLaidOut(docLine);
				}
			},
			DispatcherPriority.Loaded);

	/// <summary>
	/// Puts both panes on a row, and tries again for a few turns of the dispatcher. Giving
	/// rows back rebuilds what is collapsed, and a scroll issued before those rows have been
	/// measured lands short, or at the top of the file.
	///
	/// Between turns rather than from a layout callback: scrolling from inside a layout pass
	/// invalidates the layout that is running, and a handler waiting for passes that have not
	/// come yet is still armed when the reader scrolls - which both drags the view back to a
	/// row they have left and lays the view out until Avalonia gives up on it.
	/// </summary>
	void ScrollBothWhenLaidOut(int docLine)
	{
		int attempts = 4;
		Dispatcher.UIThread.Post(Attempt, DispatcherPriority.Background);

		void Attempt()
		{
			Left.ScrollToLine(Math.Min(docLine, Left.Document.LineCount));
			Right.ScrollToLine(Math.Min(docLine, Right.Document.LineCount));
			// Once the row is on screen there is nothing left to correct, and nothing is
			// armed to correct it later.
			if (--attempts > 0 && !Shows(Right, docLine) && !Shows(Left, docLine))
				Dispatcher.UIThread.Post(Attempt, DispatcherPriority.Background);
		}

		static bool Shows(ReviewTextEditor editor, int docLine)
		{
			var view = editor.TextArea.TextView;
			return view.VisualLinesValid
				&& view.VisualLines.Any(line => line.FirstDocumentLine.LineNumber <= docLine
					&& docLine <= line.LastDocumentLine.LineNumber);
		}
	}

	/// <summary>
	/// Gives a row back before the caret is put on it: a line hidden as context, or folded
	/// away inside a member, is not a place the caret can be seen. Both panes are opened,
	/// because they render the same rows and only stay in step while they agree on which.
	/// The gap goes first - revealing rebuilds the folds over it.
	/// </summary>
	void Reveal(int docLine)
	{
		contextGaps?.Reveal(docLine);
		foreach (var (editor, folding) in
			new[] { (Left, leftFolding), (Right, rightFolding) })
		{
			if (folding is null || docLine > editor.Document.LineCount)
				continue;
			int offset = editor.Document.GetLineByNumber(docLine).Offset;
			foreach (var section in folding.GetFoldingsContaining(offset))
				section.IsFolded = false;
		}
	}

	/// <summary>
	/// Both panes fold over the same document lines. They have to: the panes are kept in
	/// step by copying the scroll offset, which is only exact while they render the same
	/// number of lines, so a fold on one side must collapse the other side's matching rows.
	/// Unchanged context is hidden by one gap view driving both panes, for that same reason.
	/// </summary>
	void InstallFoldings(SideBySideDocumentViewModel vm)
	{
		leftFolding ??= FoldingManager.Install(Left.TextArea);
		rightFolding ??= FoldingManager.Install(Right.TextArea);
		if (contextGaps is null)
		{
			contextGaps = new ContextGapView(Left, Right);
			contextGaps.Changed += RefreshFoldings;
			leftMargin.IsContextGapRow = contextGaps.HasBar;
			rightMargin.IsContextGapRow = contextGaps.HasBar;
			ContextGapFoldingMargin.Install(Left.TextArea, leftFolding, contextGaps.HasBar);
			ContextGapFoldingMargin.Install(Right.TextArea, rightFolding, contextGaps.HasBar);
		}
		var tags = vm.Pair.LeftTags;
		bool hasChanges = tags.Any(t => t.Kind != DiffLineKind.Context);
		var ranges = structuralRanges = [];
		if (App.Workspace is { } workspace)
		{
			// Member regions come from the side the file still has, and are applied to both
			// panes: corresponding lines share a row, so the range holds on either side.
			bool oldSide = vm.File.Kind == FileChangeKind.Deleted;
			string relPath = oldSide ? vm.File.OldPath : vm.File.Path;
			var (sideText, sideToDocLine) = vm.Pair.GetSideText(oldSide);
			var regions = workspace.SemanticsFor(oldSide, relPath)
				?.GetFoldRegionsAsync(relPath, sideText, CancellationToken.None);
			// Only what is already known: this view rebuilds its folds whenever the document
			// or the gaps change, and a server's answer arrives with the next rebuild.
			if (regions is { IsCompletedSuccessfully: true })
				ranges.AddRange(DiffFolding.Members(regions.Result, sideToDocLine));
		}
		contextGaps.Install(tags, hasChanges, ranges);
		RefreshFoldings();
	}

	/// <summary>
	/// The structural folds that apply to what is shown, in both panes. A fold beginning
	/// inside hidden context is left out: the gap's control stands for those lines, and the
	/// margin would otherwise draw the fold's marker beside it.
	/// </summary>
	void RefreshFoldings()
	{
		if (leftFolding is null || rightFolding is null)
			return;
		var shown = contextGaps?.ClipToVisible(structuralRanges) ?? structuralRanges;
		leftFolding.Clear();
		rightFolding.Clear();
		leftFolding.UpdateFoldings(FoldInstaller.ToFoldings(Left.Document, shown), -1);
		rightFolding.UpdateFoldings(FoldInstaller.ToFoldings(Right.Document, shown), -1);
	}

	/// <summary>Copies collapse state across, matching sections by position in the ordered
	/// list: both panes were given the same ranges, so index i means the same rows.</summary>
	void MirrorFolds(FoldingManager? source, FoldingManager? target)
	{
		if (syncingFolds || source is null || target is null)
			return;
		syncingFolds = true;
		try
		{
			var from = source.AllFoldings.ToList();
			var to = target.AllFoldings.ToList();
			for (int i = 0; i < from.Count && i < to.Count; i++)
			{
				if (to[i].IsFolded != from[i].IsFolded)
					to[i].IsFolded = from[i].IsFolded;
			}
		}
		finally
		{
			syncingFolds = false;
		}
	}
}
