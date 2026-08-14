using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Stampeded;

/// <summary>Modal question with one named action; closes with true for it, false otherwise.</summary>
public partial class ConfirmWindow : Window
{
	public ConfirmWindow()
	{
		InitializeComponent();
	}

	public ConfirmWindow(string title, string message, string confirmLabel) : this()
	{
		Title = title;
		MessageText.Text = message;
		ConfirmButton.Content = confirmLabel;
	}

	void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

	void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
