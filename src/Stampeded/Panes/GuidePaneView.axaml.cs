using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Stampeded.Panes;

public partial class GuidePaneView : UserControl
{
	public GuidePaneView()
	{
		InitializeComponent();
	}

	GuidePaneViewModel? Vm => DataContext as GuidePaneViewModel;

	void OnPhaseClick(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && (sender as Button)?.DataContext is GuidePhase phase)
			vm.SelectPhase(phase);
	}

	void OnBounce(object? sender, RoutedEventArgs e) => Vm?.Bounce();

	void OnRecord(object? sender, RoutedEventArgs e) => Vm?.OpenRecord();

	void OnNext(object? sender, RoutedEventArgs e) => Vm?.NextPhase();

	void OnSweepDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (Vm is { } vm && SweepList.SelectedItem is ReviewWorkspace.SweepItem item)
			vm.OpenSweepItem(item);
	}
}
