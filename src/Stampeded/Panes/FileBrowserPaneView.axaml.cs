using Avalonia.Controls;
using Avalonia.Input;

namespace Stampeded.Panes;

public partial class FileBrowserPaneView : UserControl
{
	public FileBrowserPaneView()
	{
		InitializeComponent();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is FileBrowserPaneViewModel vm && Tree.SelectedItem is FsNode node)
			vm.Open(node);
	}

	/// <summary>Expands the path to a repo-relative file and selects its node. Containers
	/// materialize per expanded level, so the walk yields to layout between levels.</summary>
	public async Task RevealAsync(string relPath)
	{
		if (DataContext is not FileBrowserPaneViewModel vm)
			return;
		IEnumerable<FsNode> level = vm.Roots;
		Avalonia.Controls.ItemsControl parent = Tree;
		FsNode? node = null;
		foreach (var segment in relPath.Split('/'))
		{
			node = level.FirstOrDefault(n => n.Title == segment);
			if (node is null)
				return;
			var container = parent.ContainerFromItem(node) as Avalonia.Controls.TreeViewItem;
			if (container is null)
			{
				await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { },
					Avalonia.Threading.DispatcherPriority.Loaded);
				container = parent.ContainerFromItem(node) as Avalonia.Controls.TreeViewItem;
			}
			if (container is null)
				return;
			if (node.IsDirectory)
			{
				container.IsExpanded = true;
				await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { },
					Avalonia.Threading.DispatcherPriority.Loaded);
				parent = container;
				level = node.Children;
			}
			else
			{
				if (!ReferenceEquals(Tree.SelectedItem, node))
				{
					Tree.SelectedItem = node;
					container.BringIntoView();
				}
			}
		}
	}
}
