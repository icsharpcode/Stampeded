using Avalonia.Controls;
using Avalonia.Input;

namespace Stampeded.Panes;

public partial class PrFilesPaneView : UserControl
{
	public PrFilesPaneView()
	{
		InitializeComponent();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is PrFilesPaneViewModel vm && List.SelectedItem is FileEntry entry)
			vm.Open(entry);
	}
}
