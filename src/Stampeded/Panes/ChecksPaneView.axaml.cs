using Avalonia.Controls;
using Avalonia.Input;

namespace Stampeded.Panes;

public partial class ChecksPaneView : UserControl
{
	public ChecksPaneView()
	{
		InitializeComponent();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is ChecksPaneViewModel vm && CheckList.SelectedItem is CheckRow row)
			vm.Open(row);
	}

	void OnRefresh(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		if (DataContext is ChecksPaneViewModel vm)
			vm.LoadAsync().HandleExceptions();
	}
}
