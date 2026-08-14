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

	[ObservableProperty]
	string commitScopeLine = "Whole change";

	/// <summary>Whether the change touches decompiler test cases, which is what the
	/// fixtures-in-ILSpy command needs to have anything to open.</summary>
	[ObservableProperty]
	bool hasFixtureTools;

	public ExplorerPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		Files = new PrFilesPaneViewModel(workspace);
		Browser = new FileBrowserPaneViewModel(workspace);
		workspace.CommitScopeChanged += () => Dispatcher.UIThread.Post(UpdateCommitScope);
		workspace.ReviewChanged += () => Dispatcher.UIThread.Post(UpdateCommitScope);
		UpdateCommitScope();
	}

	void UpdateCommitScope()
	{
		HasReview = workspace.HeadSha is not null;
		HasFixtureTools = workspace.HasDecompilerTestCases;
		InCommitScope = workspace.CommitScope is not null;
		CommitScopeLine = workspace.CommitScope is { } commit
			? $"Commit {workspace.CommitScopeIndex + 1} of {workspace.ScopeCommits.Count}: {commit.Subject}"
			: "Whole change";
	}

	public void EnterCommitScope() => workspace.EnterCommitScopeAsync().HandleExceptions();

	public void StepCommit(int direction) => workspace.StepCommitScopeAsync(direction).HandleExceptions();

	public void ExitCommitScope() => workspace.ExitCommitScopeAsync().HandleExceptions();

	// The verification and close-out commands from the overview page, kept where the reading
	// happens rather than a tab away. Opening on GitHub and bouncing stay behind in the menu:
	// they belong to deciding about the change, not to reading it.
	public void OpenInVsCode() => workspace.OpenInVsCodeAsync(oldSide: false).HandleExceptions();

	public void OpenFixturesInIlspy() => workspace.OpenAffectedFixturesInILSpyAsync().HandleExceptions();

	public void OpenRecord() => workspace.OpenReviewRecord();

	public void CloseReview() => workspace.CloseReviewAsync().HandleExceptions();
}
