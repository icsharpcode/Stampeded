using Avalonia.Controls;
using Avalonia.Input;

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
		if (DataContext is PrListPaneViewModel vm && List.SelectedItem is PrSummary pr)
			vm.Open(pr);
	}
}
