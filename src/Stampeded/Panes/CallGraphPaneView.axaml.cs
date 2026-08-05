using Avalonia.Controls;
using Avalonia.Input;

namespace Stampeded.Panes;

public partial class CallGraphPaneView : UserControl
{
	public CallGraphPaneView()
	{
		InitializeComponent();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is CallGraphPaneViewModel vm && Tree.SelectedItem is CallGraphNode node)
			vm.Open(node);
	}
}
