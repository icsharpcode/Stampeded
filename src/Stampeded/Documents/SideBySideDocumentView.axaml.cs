using Avalonia.Controls;
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
		| ReviewCommands.FindReferences;

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
	public void JumpToHunkCommand(int direction)
	{
		var tags = FocusedEditor == Right ? rightTags : leftTags;
		if (tags is null || tags.Count == 0)
			return;
		var editor = FocusedEditor;
		int line = editor.TextArea.Caret.Line;
		bool InHunk(int docLine) => docLine >= 1 && docLine <= tags.Count
			&& tags[docLine - 1].Kind is not (DiffLineKind.Context or DiffLineKind.Filler);
		// Off the current run first, so stepping does not stop on the row it started in.
		int next = line;
		while (InHunk(next) && next + direction >= 1 && next + direction <= tags.Count)
			next += direction;
		while (next + direction >= 1 && next + direction <= tags.Count && !InHunk(next))
			next += direction;
		if (!InHunk(next))
			return;
		editor.TextArea.Caret.Line = next;
		editor.TextArea.Caret.Column = 1;
		editor.ScrollToLine(next);
	}

	public void GoToDefinitionCommand() => FocusedPane?.NavigateToDefinition();

	public void FindReferencesCommand() => FocusedPane?.ShowReferences();

	public void JumpToUncoveredCommand() => NotHere("Coverage");

	public void ToggleBlameCommand() => NotHere("Blame");

	public void CommentAtCaretCommand() => NotHere("Commenting");

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
		FoldViewportAnchor.Install(Left);
		FoldViewportAnchor.Install(Right);
	}

	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		ReviewViews.Register(this);
		Dispatcher.UIThread.Post(WireScrollSync, DispatcherPriority.Loaded);
		if (App.Workspace is { } ws)
			ws.SemanticsChanged += OnSemanticsChanged;
		RefreshSemantics();
	}

	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		ReviewViews.Unregister(this);
		if (App.Workspace is { } ws)
			ws.SemanticsChanged -= OnSemanticsChanged;
		base.OnDetachedFromVisualTree(e);
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
		try
		{
			target.Offset = source.Offset;
		}
		finally
		{
			syncing = false;
		}
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
		var highlighting = HighlightingService.GetForFile(
			vm.File.Path,
			() => vm.Pair.GetSideText(oldSide: vm.File.Kind == FileChangeKind.Deleted).Text);
		Left.SyntaxHighlighting = highlighting;
		Right.SyntaxHighlighting = highlighting;
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
		RefreshSemantics();
		// A caret asked for before this view had the document: navigation opens a document and
		// then says where to land, and the two need not happen in that order.
		if (vm.TakePendingCaret() is { } pending)
			OnCaretRequested(pending.Line, pending.OldSide);
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
				if (first?.MoveCaretToBlobLine(blobLine) != true)
					second?.MoveCaretToBlobLine(blobLine);
			},
			DispatcherPriority.Loaded);

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
		if (vm.File.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
		{
			// Member regions come from the side the file still has, and are applied to both
			// panes: corresponding lines share a row, so the range holds on either side.
			bool oldSide = vm.File.Kind == FileChangeKind.Deleted;
			var (sideText, sideToDocLine) = vm.Pair.GetSideText(oldSide);
			ranges.AddRange(DiffFolding.Members(sideText, sideToDocLine));
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
