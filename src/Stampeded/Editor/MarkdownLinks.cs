using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

namespace Stampeded.Editor;

/// <summary>
/// Markdown rendering that opens what it draws. The engine takes a command to run when a
/// link is pressed, and without one a link is decoration: it renders as a link, and pressing
/// it does nothing.
/// </summary>
public static class MarkdownLinks
{
	public static ICommand OpenCommand { get; } = new RelayCommand<string>(url => {
		if (!string.IsNullOrWhiteSpace(url))
			App.Workspace?.OpenUrlAsync(url).HandleExceptions();
	});

	public static global::Markdown.Avalonia.Markdown NewEngine() => new() { HyperlinkCommand = OpenCommand };
}
