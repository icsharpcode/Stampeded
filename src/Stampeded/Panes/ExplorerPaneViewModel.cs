using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

namespace Stampeded.Panes;

/// <summary>
/// The left-hand explorer: the review's changed files (viewed, depth, coverage state)
/// above the full worktree tree - one pane, VS Code style, instead of two tabs.
/// </summary>
public partial class ExplorerPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;

	public PrFilesPaneViewModel Files { get; }
	public FileBrowserPaneViewModel Browser { get; }

	/// <summary>Whether the change is being read one commit at a time; the stepper sits
	/// above the file list because that list is what the commit scopes.</summary>
	[ObservableProperty]
	bool inCommitScope;

	/// <summary>The bar states the reading scope whenever a review is open, so the choice
	/// is visible from where the files are rather than only once it has been made.</summary>
	[ObservableProperty]
	bool hasReview;

	/// <summary>Whether the review is narrowed to anything less than the whole change - by a
	/// commit or by what has changed since the last pass. Both leave the same way.</summary>
	[ObservableProperty]
	bool inScope;

	/// <summary>Whether there is an earlier pass to compare against.</summary>
	[ObservableProperty]
	bool canEnterSinceLastPass;

	/// <summary>Whether reading the change one commit at a time says anything here: a single
	/// commit is the whole change already.</summary>
	[ObservableProperty]
	bool canEnterCommitScope;

	/// <summary>What the two scope commands say of themselves - what they do, or why they
	/// cannot. A disabled control that explains nothing is a control the reader has to guess at.</summary>
	[ObservableProperty]
	string commitScopeTip = "";

	[ObservableProperty]
	string sinceLastPassTip = "";

	/// <summary>The heading over the file list, carrying how much there is to read. It counts
	/// what the list holds, so in a scope it is the scope's size and not the review's.</summary>
	[ObservableProperty]
	string changedFilesHeader = "CHANGED FILES";

	/// <summary>What is being read, in two or three words. A scope changes what every list and
	/// every diff in the window means, and a reader who has forgotten which one they are in
	/// reads the wrong thing without noticing - so it is named where the files are, not only
	/// in the sentence describing it.</summary>
	[ObservableProperty]
	string scopeBadge = "";

	[ObservableProperty]
	string commitScopeLine = "Whole change";

	/// <summary>The message of the commit being read, as it was written. In per-commit mode
	/// it is the author's account of why the change is what it is, which is the thing a
	/// reader wants before the diff - and too long for the one-line scope header.</summary>
	[ObservableProperty]
	string commitMessage = "";

	/// <summary>Whether the review is of a pull request, which is what there is to open on
	/// GitHub; a local range has no page.</summary>
	[ObservableProperty]
	bool hasPullRequest;

	public ExplorerPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		Files = new PrFilesPaneViewModel(workspace);
		Browser = new FileBrowserPaneViewModel(workspace);
		workspace.Scopes.Changed += () => Dispatcher.UIThread.Post(UpdateCommitScope);
		workspace.ReviewChanged += () => Dispatcher.UIThread.Post(UpdateCommitScope);
		UpdateCommitScope();
	}

	void UpdateCommitScope()
	{
		HasReview = workspace.HeadSha is not null;
		ChangedFilesHeader = workspace.HeadSha is null
			? "CHANGED FILES"
			: $"CHANGED FILES  ({workspace.Files.Count})";
		HasPullRequest = workspace.CurrentPr is not null;
		InCommitScope = workspace.Scopes.Commit is not null;
		InScope = workspace.Scopes.InScope;
		CanEnterSinceLastPass = workspace.Scopes.CanEnterSinceLastPass;
		CanEnterCommitScope = workspace.Scopes.CanEnterCommit;
		CommitScopeTip = workspace.Scopes.CommitScopeTip;
		SinceLastPassTip = workspace.Scopes.SinceLastPassTip;
		// The working tree is the last entry of the series but is not a commit, and a badge
		// reading "COMMIT 3 OF 3" over "not committed yet" contradicts itself.
		string position = $"{workspace.Scopes.CommitIndex + 1} OF {workspace.Scopes.Series.Count}";
		ScopeBadge = workspace.Scopes.Commit is { } badged
			? badged.IsWorkingTree ? $"UNCOMMITTED, {position}" : $"COMMIT {position}"
			: workspace.Scopes.InSinceLastPass ? "SINCE YOUR LAST PASS"
			: "";
		CommitScopeLine = workspace.Scopes.Commit is { } commit
			? commit.IsWorkingTree
				? $"Uncommitted work, {position.ToLowerInvariant()}: {commit.Subject}"
				: $"Commit {position.ToLowerInvariant()}: {commit.Subject}"
			: workspace.Scopes.InSinceLastPass ? workspace.Scopes.ScopeLine
			: "Whole change";
		CommitMessage = workspace.Scopes.Commit?.Message ?? "";
	}

	public void EnterCommitScope() => workspace.Scopes.EnterCommitAsync().HandleExceptions();

	public void EnterSinceLastPass() => workspace.Scopes.EnterSinceLastPassAsync().HandleExceptions();

	public void StepCommit(int direction) => workspace.Scopes.StepCommitAsync(direction).HandleExceptions();

	public void ExitCommitScope() => workspace.Scopes.ExitAsync().HandleExceptions();

	// The verification and close-out commands from the overview page, kept where the reading
	// happens rather than a tab away. Bouncing stays behind in the menu: it belongs to
	// deciding about the change, not to reading it.
	public void OpenInVsCode() => workspace.OpenInVsCodeAsync(oldSide: false).HandleExceptions();

	public void OpenPrOnGitHub()
	{
		if (workspace.CurrentPr is { } pr)
			workspace.OpenOnGitHubAsync(pr.Number).HandleExceptions();
	}

	public void OpenReview() => workspace.OpenReviewDocument();


	public void CloseReview() => workspace.CloseReviewAsync().HandleExceptions();

	public void ReloadReview() => workspace.ReloadReviewAsync().HandleExceptions();
}
