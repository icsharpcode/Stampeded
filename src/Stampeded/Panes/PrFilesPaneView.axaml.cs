using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Stampeded.Panes;

public partial class PrFilesPaneView : UserControl
{
	public PrFilesPaneView()
	{
		InitializeComponent();
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
		if (DataContext is PrFilesPaneViewModel vm && List.SelectedItem is FileEntry entry)
			vm.Open(entry);
	}

	void OnToggleViewedClicked(object? sender, RoutedEventArgs e)
	{
		if (List.SelectedItem is FileEntry entry)
			entry.IsViewed = !entry.IsViewed;
	}

	void SetDepth(string depth)
	{
		if (DataContext is PrFilesPaneViewModel vm && List.SelectedItem is FileEntry entry)
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
		if (entry is null || ReferenceEquals(List.SelectedItem, entry))
			return;
		List.SelectedItem = entry;
		List.ScrollIntoView(entry);
	}

	void OnDepthDeep(object? sender, RoutedEventArgs e) => SetDepth("deep");

	void OnDepthSkim(object? sender, RoutedEventArgs e) => SetDepth("skim");

	void OnDepthTrust(object? sender, RoutedEventArgs e) => SetDepth("trust");
}
