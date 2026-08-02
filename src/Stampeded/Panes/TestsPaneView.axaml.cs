using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Stampeded.Panes;

public partial class TestsPaneView : UserControl
{
	public static readonly IValueConverter RunButtonText =
		new FuncValueConverter<bool, string>(running => running ? "Cancel" : "Run");

	public TestsPaneView()
	{
		InitializeComponent();
	}

	void OnRunClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is TestsPaneViewModel vm)
			vm.Run();
	}

	void OnDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (DataContext is TestsPaneViewModel vm && List.SelectedItem is TestRow row)
			vm.Open(row);
	}
}
