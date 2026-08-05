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
			Tree.SelectedItem = node;
			Tree.ScrollIntoView(node);
		}
		return Task.CompletedTask;
	}
}
