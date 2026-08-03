using Avalonia.Controls;

namespace Stampeded.Panes;

public partial class ChangeMapPaneView : UserControl
{
	public ChangeMapPaneView()
	{
		InitializeComponent();
	}

	void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (DataContext is ChangeMapPaneViewModel vm && List.SelectedItem is ChangeMapRow row)
			vm.Open(row);
	}
}
