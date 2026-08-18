using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Avalonia.VisualTree;

namespace Stampeded.Panes;

public partial class PrFilesPaneView : UserControl
{
	/// <summary>True while a selection is being set to follow a document, so that echo does
	/// not reopen what is already open.</summary>
	bool revealing;

	public PrFilesPaneView()
	{
		InitializeComponent();
	}

	/// <summary>
	/// Selecting a file opens it. Files are not opened when a review loads any more - the
	/// list is the review's queue, and walking it with the mouse or the arrow keys is how a
	/// reader gets to the next one.
	/// </summary>
	void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (!revealing)
			OpenSelected();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		OpenSelected();
	}

	void OnOpenClicked(object? sender, RoutedEventArgs e)
	{
		OpenSelected();
	}

	/// <summary>The selected file, or null when a directory row is selected.</summary>
	FileEntry? Selected => (FileTree.SelectedItem as FileNode)?.Entry;

	void OpenSelected()
	{
		if (DataContext is PrFilesPaneViewModel vm && Selected is { } entry)
			vm.Open(entry);
	}

	void OnToggleViewedClicked(object? sender, RoutedEventArgs e)
	{
		if (Selected is { } entry)
			entry.IsViewed = !entry.IsViewed;
	}

	/// <summary>Selects and reveals the row for a repo-relative path, if listed.</summary>
	public void RevealFile(string relPath)
	{
		if (DataContext is not PrFilesPaneViewModel vm)
			return;
		var node = Find(vm.Roots, relPath);
		if (node is null || ReferenceEquals(FileTree.SelectedItem, node))
			return;
		revealing = true;
		try
		{
			FileTree.SelectedItem = node;
		}
		finally
		{
			revealing = false;
		}
		// The row is reached through its container: a tree's rows are nested, so there is no
		// index into the pane's own items to scroll to.
		FileTree.UpdateLayout();
		foreach (var item in FileTree.GetVisualDescendants().OfType<TreeViewItem>())
		{
			if (ReferenceEquals(item.DataContext, node))
			{
				item.BringIntoView();
				break;
			}
		}
	}

	static FileNode? Find(IEnumerable<FileNode> nodes, string relPath)
	{
		foreach (var node in nodes)
		{
			if (node.Entry?.File.Path == relPath)
				return node;
			if (Find(node.Children, relPath) is { } found)
				return found;
		}
		return null;
	}
}
