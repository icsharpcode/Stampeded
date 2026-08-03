using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Stampeded;

public partial class UrlPromptWindow : Window
{
	public UrlPromptWindow()
	{
		InitializeComponent();
		Opened += (_, _) => UrlBox.Focus();
	}

	void OnOk(object? sender, RoutedEventArgs e) => Close(UrlBox.Text);

	void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
