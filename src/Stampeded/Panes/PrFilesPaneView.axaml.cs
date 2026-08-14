using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Stampeded.Controls;

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

	void OpenSelected()
	{
		if (DataContext is PrFilesPaneViewModel vm && FileList.SelectedItem is FileEntry entry)
			vm.Open(entry);
	}

	void OnToggleViewedClicked(object? sender, RoutedEventArgs e)
	{
		if (FileList.SelectedItem is FileEntry entry)
			entry.IsViewed = !entry.IsViewed;
	}

	void SetDepth(string depth)
	{
		if (DataContext is PrFilesPaneViewModel vm && FileList.SelectedItem is FileEntry entry)
		{
			vm.SetDepth(entry, depth);
			entry.Depth = depth;
		}
	}

	/// <summary>Selects and reveals the entry for a repo-relative path, if listed.</summary>
	public void RevealFile(string relPath)
	{
		if (DataContext is not PrFilesPaneViewModel vm)
			return;
		var entry = vm.Files.FirstOrDefault(f => f.File.Path == relPath);
		if (entry is null || ReferenceEquals(FileList.SelectedItem, entry))
			return;
		revealing = true;
		try
		{
			FileList.SelectedItem = entry;
		}
		finally
		{
			revealing = false;
		}
		FileList.ScrollRowIntoView(entry);
	}

	void OnDepthDeep(object? sender, RoutedEventArgs e) => SetDepth("deep");

	void OnDepthSkim(object? sender, RoutedEventArgs e) => SetDepth("skim");

	void OnDepthTrust(object? sender, RoutedEventArgs e) => SetDepth("trust");
}
