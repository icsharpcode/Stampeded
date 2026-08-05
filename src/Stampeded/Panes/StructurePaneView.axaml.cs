using Avalonia.Controls;
using Avalonia.Input;

namespace Stampeded.Panes;

public partial class StructurePaneView : UserControl
{
	public StructurePaneView()
	{
		InitializeComponent();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is StructurePaneViewModel vm && Tree.SelectedItem is StructureNode node)
			vm.Open(node);
	}
}
