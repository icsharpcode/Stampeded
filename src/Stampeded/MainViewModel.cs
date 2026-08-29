using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Controls;

using Stampeded.Docking;

namespace Stampeded;

public partial class MainViewModel : ObservableObject
{
	public IRootDock Layout { get; }

	public BusyTracker Busy { get; }

	public ObservableCollection<string> Recent { get; }

	[ObservableProperty]
	string windowTitle = "Stampeded!";

	/// <summary>Whether the commands that need a compilation can be offered yet; a review is
	/// open and readable long before its semantics have loaded.</summary>
	[ObservableProperty]
	bool semanticsReady;

	/// <summary>
	/// What the menu bar may offer. A command that cannot run is greyed rather than left to
	/// fail silently: with no review open, "Close Review" that does nothing reads as a broken
	/// command instead of an unavailable one.
	/// </summary>
	[ObservableProperty]
	bool hasReview;

	[ObservableProperty]
	bool hasPullRequest;

	[ObservableProperty]
	bool canComment;

	[ObservableProperty]
	bool inScope;

	/// <summary>The whole change is what is being read - which is not the same as no scope
	/// being set: with no review open, nothing is being read at all and the scope marks
	/// belong to none of the three.</summary>
	[ObservableProperty]
	bool wholeChangeActive;

	[ObservableProperty]
	bool inCommitScope;

	[ObservableProperty]
	bool inSinceLastPassScope;

	[ObservableProperty]
	bool canEnterCommitScope;

	[ObservableProperty]
	bool canEnterSinceLastPass;

	/// <summary>What the two scope entries say of themselves, so a greyed one gives its reason
	/// rather than only its name.</summary>
	[ObservableProperty]
	string commitScopeTip = "";

	[ObservableProperty]
	string sinceLastPassTip = "";

	/// <summary>Which point "since last pass" measures from, and whether this review has one
	/// of that kind at all - a review nobody has ticked a file off in has nothing to offer
	/// under the first of them, and the entry says so instead of doing nothing.</summary>
	[ObservableProperty]
	bool passFromMarked;

	[ObservableProperty]
	bool passFromSubmitted;

	[ObservableProperty]
	bool passFromOpened;

	[ObservableProperty]
	bool hasPassFromMarked;

	[ObservableProperty]
	bool hasPassFromSubmitted;

	[ObservableProperty]
	bool hasPassFromOpened;

	[ObservableProperty]
	string passFromMarkedTip = "";

	[ObservableProperty]
	string passFromSubmittedTip = "";

	[ObservableProperty]
	string passFromOpenedTip = "";

	[ObservableProperty]
	bool hasDecompilerTestCases;

	[ObservableProperty]
	Documents.StartDocumentViewModel? startPage;

	/// <summary>Scale of the whole window's content, as it was left last time. Steps are
	/// multiplicative so each press changes the size by as much as the last one appeared to.</summary>
	[ObservableProperty]
	double zoom = ZoomPreference.Load(MinZoom, MaxZoom);

	const double ZoomStep = 1.1;
	const double MinZoom = 0.5;
	const double MaxZoom = 3.0;

	public void ZoomIn() => Zoom = Math.Min(MaxZoom, Zoom * ZoomStep);

	public void ZoomOut() => Zoom = Math.Max(MinZoom, Zoom / ZoomStep);

	public void ZoomReset() => Zoom = 1.0;

	partial void OnZoomChanged(double value)
	{
		ZoomState.Set(value);
		// Written as it changes rather than on the way out: a session that ends by being killed
		// still meant its last zoom.
		ZoomPreference.Save(value);
	}

	public MainViewModel()
	{
		// Before anything reads the list. Both views that show it - this menu and the start
		// page - snapshot it while this constructor runs, so a repository recorded at the end
		// of it was missing from both until the next start.
		// The popups are scaled by a transform of their own, which the property setter keeps in
		// step - but nothing set it, so a remembered zoom has to hand it over here.
		ZoomState.Set(Zoom);
		RecentRepos.Record(Program.RepoPath);
		Recent = new(RecentRepos.Load());
		var workspace = new ReviewWorkspace(Program.RepoPath);
		Busy = workspace.Busy;
		App.Workspace = workspace;
		var factory = new StampededDockFactory(workspace);
		Layout = factory.CreateLayout();
		factory.InitLayout(Layout);
		workspace.Factory = factory;
		workspace.Documents = factory.Documents;
		workspace.OpenStart();
		StartPage = workspace.StartPage;
		workspace.ReviewChanged += UpdateTitle;
		workspace.ReviewChanged += RefreshReviewState;
		workspace.Scopes.Changed += RefreshReviewState;
		workspace.SemanticsChanged += () =>
			Avalonia.Threading.Dispatcher.UIThread.Post(() => SemanticsReady = workspace.SemanticsReady);
		UpdateTitle();
		RefreshReviewState();
	}

	/// <summary>Reads the review's state in one place, on the events that can change it. The
	/// state of the tab in front is not here: it has no event, and the menu reads it when it
	/// opens instead.</summary>
	void RefreshReviewState()
	{
		var workspace = App.Workspace;
		HasReview = workspace is { CurrentPr: not null } or { LocalRange: not null };
		HasPullRequest = workspace?.CurrentPr is not null;
		CanComment = workspace?.Comments.CanComment ?? false;
		InScope = workspace?.Scopes.InScope ?? false;
		WholeChangeActive = HasReview && !InScope;
		InCommitScope = workspace?.Scopes.Commit is not null;
		InSinceLastPassScope = workspace?.Scopes.InSinceLastPass ?? false;
		CanEnterCommitScope = workspace?.Scopes.CanEnterCommit ?? false;
		CanEnterSinceLastPass = workspace?.Scopes.CanEnterSinceLastPass ?? false;
		UpdatePassBaselines(workspace);
		CommitScopeTip = workspace?.Scopes.CommitScopeTip ?? "";
		SinceLastPassTip = workspace?.Scopes.SinceLastPassTip ?? "";
		HasDecompilerTestCases = workspace?.HasDecompilerTestCases ?? false;
		ScopePalette.Set(workspace);
	}

	/// <summary>What each of the three points has to offer, for the entries that choose one.
	/// The list is the scope's, so the menu bar and the button's dropdown say the same thing.</summary>
	void UpdatePassBaselines(ReviewWorkspace? workspace)
	{
		var options = workspace?.Scopes.PassBaselineOptions ?? [];
		PassFromMarked = InUse(PassBaselineKind.MarkedViewed);
		PassFromSubmitted = InUse(PassBaselineKind.SubmittedReview);
		PassFromOpened = InUse(PassBaselineKind.Opened);
		HasPassFromMarked = Available(PassBaselineKind.MarkedViewed);
		HasPassFromSubmitted = Available(PassBaselineKind.SubmittedReview);
		HasPassFromOpened = Available(PassBaselineKind.Opened);
		PassFromMarkedTip = Tip(PassBaselineKind.MarkedViewed);
		PassFromSubmittedTip = Tip(PassBaselineKind.SubmittedReview);
		PassFromOpenedTip = Tip(PassBaselineKind.Opened);

		PassBaselineOption? Option(PassBaselineKind kind) => options.FirstOrDefault(o => o.Kind == kind);
		bool InUse(PassBaselineKind kind) => Option(kind)?.InUse ?? false;
		bool Available(PassBaselineKind kind) => Option(kind)?.Available ?? false;
		string Tip(PassBaselineKind kind) => Option(kind)?.Tip ?? "";
	}

	void UpdateTitle()
	{
		string repo = $"{Path.GetFileName(Program.RepoPath)}  ({Program.RepoPath})";
		WindowTitle = App.Workspace switch {
			{ CurrentPr: { } pr } => $"Stampeded!  -  {repo}  -  PR #{pr.Number}",
			{ LocalRange: { } range } => $"Stampeded!  -  {repo}  -  {range.Head}",
			_ => $"Stampeded!  -  {repo}",
		};
	}
}
