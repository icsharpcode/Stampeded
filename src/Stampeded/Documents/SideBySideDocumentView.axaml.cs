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
public partial class SideBySideDocumentView : UserControl
{
	readonly DiffLineNumberMargin leftMargin = new();
	readonly DiffLineNumberMargin rightMargin = new();
	SideBySidePane? leftPane;
	SideBySidePane? rightPane;
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
		Dispatcher.UIThread.Post(WireScrollSync, DispatcherPriority.Loaded);
		if (App.Workspace is { } ws)
			ws.SemanticsChanged += OnSemanticsChanged;
		RefreshSemantics();
	}

	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
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
		if (DataContext is not SideBySideDocumentViewModel vm)
			return;
		var highlighting = HighlightingService.GetByExtension(Path.GetExtension(vm.File.Path));
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
		contextGaps.Install(tags, hasChanges);
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
		var shown = structuralRanges.Where(r => contextGaps?.Hides(r.StartLine) != true).ToList();
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
