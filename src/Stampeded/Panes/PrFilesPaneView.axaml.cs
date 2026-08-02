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
}
