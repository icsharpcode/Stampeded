using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Stampeded.Panes;

public partial class RunPaneView : UserControl
{
	public static readonly IValueConverter RunButtonText =
		new FuncValueConverter<bool, string>(running => running ? "Stop" : "Run");

	RunPaneViewModel? viewModel;

	public RunPaneView()
	{
		InitializeComponent();
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (viewModel is not null)
			viewModel.State.PropertyChanged -= OnStateChanged;
		viewModel = DataContext as RunPaneViewModel;
		if (viewModel is not null)
			viewModel.State.PropertyChanged += OnStateChanged;
	}

	void OnStateChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(RunState.Output))
			Dispatcher.UIThread.Post(() => OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0);
	}

	void OnRunClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is RunPaneViewModel vm)
			vm.Run();
	}

	void OnClearClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is RunPaneViewModel vm)
			vm.State.Output = "";
	}
}
