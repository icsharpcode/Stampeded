using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Stampeded.Panes;

public partial class TestsPaneView : UserControl
{
	/// <summary>The run button is also the cancel button: one place to press, and its icon
	/// says which of the two it currently is.</summary>
	public static readonly IValueConverter RunButtonIcon =
		new FuncValueConverter<bool, Avalonia.Media.IImage>(running => running ? Images.Cancel : Images.RunTest);

	public static readonly IValueConverter RunButtonTip =
		new FuncValueConverter<bool, string>(running => running ? "Cancel the running tests" : "Run the tests");

	TestsPaneViewModel? viewModel;

	public TestsPaneView()
	{
		InitializeComponent();
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (viewModel is not null)
			viewModel.State.PropertyChanged -= OnStateChanged;
		viewModel = DataContext as TestsPaneViewModel;
		if (viewModel is not null)
			viewModel.State.PropertyChanged += OnStateChanged;
	}

	void OnStateChanged(object? sender, PropertyChangedEventArgs e)
	{
		// Follow the live output like a terminal: keep the caret (and thus the
		// viewport) pinned to the end as new chunks arrive.
		if (e.PropertyName == nameof(TestsState.Output))
			Dispatcher.UIThread.Post(() => OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0);
	}

	void OnClearClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is TestsPaneViewModel vm)
			vm.State.Output = "";
	}

	void OnRunClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is TestsPaneViewModel vm)
			vm.Run();
	}

	void OnImpactedFilterClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is TestsPaneViewModel vm)
			vm.ApplyImpactedFilter();
	}

	void OnRunCoverageClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is TestsPaneViewModel vm)
			vm.RunWithCoverage();
	}

	void OnRunABClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is TestsPaneViewModel vm)
			vm.RunAB();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is TestsPaneViewModel vm && FailureList.SelectedItem is TestRow row)
			vm.Open(row);
	}
}
