using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Stampeded;

/// <summary>Modal one-line text prompt; closes with the entered text, or null on cancel.</summary>
public partial class TextPromptWindow : Window
{
	public TextPromptWindow()
	{
		InitializeComponent();
		Opened += (_, _) => InputBox.Focus();
	}

	public TextPromptWindow(string title, string prompt, string okLabel, string watermark = "", string initialText = "")
		: this()
	{
		Title = title;
		PromptText.Text = prompt;
		OkButton.Content = okLabel;
		InputBox.PlaceholderText = watermark;
		InputBox.Text = initialText;
		Opened += (_, _) => InputBox.SelectAll();
	}

	void OnOk(object? sender, RoutedEventArgs e) => Close(InputBox.Text);

	void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
