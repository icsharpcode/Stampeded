using Avalonia.Controls;
using Avalonia.Input;

namespace Stampeded.Panes;

public partial class CommitsPaneView : UserControl
{
	public CommitsPaneView()
	{
		InitializeComponent();
	}

	void OnCommitSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (DataContext is CommitsPaneViewModel vm && List.SelectedItem is CommitRow row)
			vm.SelectCommit(row);
	}

	void OnFileOpened(object? sender, TappedEventArgs e)
	{
		if (DataContext is CommitsPaneViewModel vm && FilesList.SelectedItem is CommitFileRow row)
			vm.OpenFile(row);
	}
}
