using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Stampeded.Panes;

public partial class MergeQueuePaneView : UserControl
{
	public MergeQueuePaneView()
	{
		InitializeComponent();
	}

	void OnRefresh(object? sender, RoutedEventArgs e)
	{
		if (DataContext is MergeQueuePaneViewModel vm)
			vm.LoadAsync().HandleExceptions();
	}

	void OnEnqueue(object? sender, RoutedEventArgs e)
	{
		if (DataContext is MergeQueuePaneViewModel vm)
			vm.EnqueueCurrentAsync(MergeMethodPreference.Load()).HandleExceptions();
	}

	void OnBreakLock(object? sender, RoutedEventArgs e)
	{
		if (DataContext is MergeQueuePaneViewModel vm)
			vm.BreakLockAsync().HandleExceptions();
	}

	void OnClear(object? sender, RoutedEventArgs e)
	{
		if (DataContext is MergeQueuePaneViewModel vm)
			vm.ClearAsync().HandleExceptions();
	}

	void OnClearErrors(object? sender, RoutedEventArgs e)
	{
		if (DataContext is MergeQueuePaneViewModel vm)
			vm.ClearErrorsAsync().HandleExceptions();
	}

	void OnRemove(object? sender, RoutedEventArgs e)
	{
		if (DataContext is MergeQueuePaneViewModel vm && QueueList.SelectedItem is MergeQueueRow row)
			vm.RemoveAsync(row).HandleExceptions();
	}

	void OnUp(object? sender, RoutedEventArgs e) => Move(-1);

	void OnDown(object? sender, RoutedEventArgs e) => Move(1);

	void Move(int delta)
	{
		if (DataContext is MergeQueuePaneViewModel vm && QueueList.SelectedItem is MergeQueueRow row)
			vm.MoveAsync(row, delta).HandleExceptions();
	}
}
