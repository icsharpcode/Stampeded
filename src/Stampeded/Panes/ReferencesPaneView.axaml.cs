using Avalonia.Controls;
using Avalonia.Input;

namespace Stampeded.Panes;

public partial class ReferencesPaneView : UserControl
{
	public ReferencesPaneView()
	{
		InitializeComponent();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is ReferencesPaneViewModel vm && List.SelectedItem is ReferenceRow row)
			vm.Open(row);
	}
}
