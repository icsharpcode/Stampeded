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

	[ObservableProperty]
	string commitScopeLine = "";

	public ExplorerPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		Files = new PrFilesPaneViewModel(workspace);
		Browser = new FileBrowserPaneViewModel(workspace);
		workspace.CommitScopeChanged += () => Dispatcher.UIThread.Post(UpdateCommitScope);
		UpdateCommitScope();
	}

	void UpdateCommitScope()
	{
		InCommitScope = workspace.CommitScope is not null;
		CommitScopeLine = workspace.CommitScope is { } commit
			? $"Commit {workspace.CommitScopeIndex + 1} of {workspace.ScopeCommits.Count}: {commit.Subject}"
			: "";
	}

	public void StepCommit(int direction) => workspace.StepCommitScopeAsync(direction).HandleExceptions();

	public void ExitCommitScope() => workspace.ExitCommitScopeAsync().HandleExceptions();
}
