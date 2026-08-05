using Avalonia.Controls;
using Avalonia.Input;

namespace Stampeded.Panes;

public partial class HistoryPaneView : UserControl
{
	public HistoryPaneView()
	{
		InitializeComponent();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is HistoryPaneViewModel vm && HistoryList.SelectedItem is CommitRow row)
			vm.Open(row);
	}
}
