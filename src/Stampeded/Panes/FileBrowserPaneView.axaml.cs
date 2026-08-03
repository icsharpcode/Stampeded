using Avalonia.Controls;
using Avalonia.Input;

namespace Stampeded.Panes;

public partial class FileBrowserPaneView : UserControl
{
	public FileBrowserPaneView()
	{
		InitializeComponent();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is FileBrowserPaneViewModel vm && Tree.SelectedItem is FsNode node)
			vm.Open(node);
	}
}
