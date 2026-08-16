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

	[ObservableProperty]
	bool hasDecompilerTestCases;

	[ObservableProperty]
	Documents.StartDocumentViewModel? startPage;

	/// <summary>Scale of the whole window's content. Steps are multiplicative so each press
	/// changes the size by as much as the last one appeared to.</summary>
	[ObservableProperty]
	double zoom = 1.0;

	const double ZoomStep = 1.1;
	const double MinZoom = 0.5;
	const double MaxZoom = 3.0;

	public void ZoomIn() => Zoom = Math.Min(MaxZoom, Zoom * ZoomStep);

	public void ZoomOut() => Zoom = Math.Max(MinZoom, Zoom / ZoomStep);

	public void ZoomReset() => Zoom = 1.0;

	partial void OnZoomChanged(double value) => ZoomState.Set(value);

	public MainViewModel()
	{
		// Before anything reads the list. Both views that show it - this menu and the start
		// page - snapshot it while this constructor runs, so a repository recorded at the end
		// of it was missing from both until the next start.
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
		workspace.CommitScopeChanged += RefreshReviewState;
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
		CanComment = workspace?.CanComment ?? false;
		InScope = workspace?.InScope ?? false;
		WholeChangeActive = HasReview && !InScope;
		InCommitScope = workspace?.CommitScope is not null;
		InSinceLastPassScope = workspace?.InSinceLastPassScope ?? false;
		CanEnterCommitScope = workspace?.CanEnterCommitScope ?? false;
		CanEnterSinceLastPass = workspace?.CanEnterSinceLastPassScope ?? false;
		HasDecompilerTestCases = workspace?.HasDecompilerTestCases ?? false;
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
