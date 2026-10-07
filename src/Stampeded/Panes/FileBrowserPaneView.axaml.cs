using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

using Stampeded;
using Stampeded.Controls.TreeView;

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

	void OnPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (e.GetCurrentPoint(Tree).Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
			return;
		if (e.Source is Avalonia.Visual source
			&& source.FindAncestorOfType<SharpTreeViewItem>(includeSelf: true)?.Node is FsNode node)
		{
			Tree.SelectedItem = node;
		}
	}

	void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
	{
		var target = CopyTarget;
		bool hasTarget = target is not null;
		CopyFileNameItem.IsEnabled = hasTarget;
		CopyPathItem.IsEnabled = hasTarget;
		CopyRelativePathItem.IsEnabled = hasTarget;
	}

	FilePathCopyTarget? CopyTarget => Tree.SelectedItem is FsNode node ? PathCopy.ForAbsolute(node.AbsPath) : null;

	void OnCopyFileName(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => PathCopy.CopyFileName(this, CopyTarget);

	void OnCopyPath(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => PathCopy.CopyPath(this, CopyTarget);

	void OnCopyRelativePath(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => PathCopy.CopyRelativePath(this, CopyTarget);
}
