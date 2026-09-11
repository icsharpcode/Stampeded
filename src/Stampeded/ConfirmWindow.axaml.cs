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

	/// <summary>The same question with a checkbox under it - something the action would also
	/// do, that the reader may or may not want this time. Read <see cref="OptionChecked"/>
	/// after the dialog closes; it holds whatever the box was left at, confirmed or not.</summary>
	public ConfirmWindow(string title, string message, string confirmLabel,
		string optionLabel, bool optionChecked, string? optionTip = null) : this(title, message, confirmLabel)
	{
		OptionCheck.Content = optionLabel;
		OptionCheck.IsChecked = optionChecked;
		OptionCheck.IsVisible = true;
		if (optionTip is not null)
			ToolTip.SetTip(OptionCheck, optionTip);
	}

	public bool OptionChecked => OptionCheck.IsChecked == true;

	void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

	void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
