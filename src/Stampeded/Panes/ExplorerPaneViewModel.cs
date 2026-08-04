using Dock.Model.Mvvm.Controls;

namespace Stampeded.Panes;

/// <summary>
/// The left-hand explorer: the review's changed files (viewed, depth, coverage state)
/// above the full worktree tree - one pane, VS Code style, instead of two tabs.
/// </summary>
public class ExplorerPaneViewModel : Tool
{
	public PrFilesPaneViewModel Files { get; }
	public FileBrowserPaneViewModel Browser { get; }

	public ExplorerPaneViewModel(ReviewWorkspace workspace)
	{
		Files = new PrFilesPaneViewModel(workspace);
		Browser = new FileBrowserPaneViewModel(workspace);
	}
}
