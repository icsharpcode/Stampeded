using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Stampeded.Core.GitHub;

namespace Stampeded.Panes;

public partial class PrListPaneView : UserControl
{
	public PrListPaneView()
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
		if (DataContext is PrListPaneViewModel vm && List.SelectedItem is PrSummary pr)
			vm.Open(pr);
	}

	void OnRefreshClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is PrListPaneViewModel vm)
			vm.LoadAsync().HandleExceptions();
	}
}
