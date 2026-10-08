using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Stampeded;

/// <summary>Modal question with one or two named actions; closes with the caller's result.</summary>
public partial class ConfirmWindow : Window
{
	object? confirmResult = true;
	object? alternateResult;
	object? cancelResult = false;

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

	public ConfirmWindow(string title, string message,
		string confirmLabel, object confirmResult,
		string alternateLabel, object alternateResult,
		object? cancelResult = null) : this(title, message, confirmLabel)
	{
		this.confirmResult = confirmResult;
		this.alternateResult = alternateResult;
		this.cancelResult = cancelResult;
		AlternateButton.Content = alternateLabel;
		AlternateButton.IsVisible = true;
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

	void OnConfirm(object? sender, RoutedEventArgs e) => Close(confirmResult);

	void OnAlternate(object? sender, RoutedEventArgs e) => Close(alternateResult);

	void OnCancel(object? sender, RoutedEventArgs e) => Close(cancelResult);
}
