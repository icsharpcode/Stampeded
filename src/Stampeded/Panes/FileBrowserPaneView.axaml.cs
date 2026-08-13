using Avalonia.Controls;

namespace Stampeded.Panes;

public partial class FileBrowserPaneView : UserControl
{
	public FileBrowserPaneView()
	{
		InitializeComponent();
	}

	/// <summary>Expands to a repo-relative file and selects it. The flattened tree makes
	/// this a model walk plus a selection, with no per-level container to wait for.</summary>
	public Task RevealAsync(string relPath)
	{
		if (DataContext is FileBrowserPaneViewModel vm && vm.Reveal(relPath) is { } node)
		{
			bool wasVisible = Tree.IsNodeFullyVisible(node);
			Tree.SelectedItem = node;
			// Posted, and skipped for a row already on screen, for the same reasons
			// TreeSelectionBinder does both: expanding the path reshapes the tree and leaves
			// the panel mid-arrange, and scrolling into that state is what strands a container
			// to paint this file over an unrelated row. Scrolling a row that is already visible
			// is work that can only go wrong.
			Avalonia.Threading.Dispatcher.UIThread.Post(() => {
				if (!wasVisible)
					Tree.ScrollIntoNodeView(node);
			});
		}
		return Task.CompletedTask;
	}
}
