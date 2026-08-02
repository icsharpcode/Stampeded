using Avalonia.Controls;
using Avalonia.Interactivity;

using Stampeded.Documents;

namespace Stampeded;

public partial class MainWindow : Window
{
	public MainWindow()
	{
		InitializeComponent();
		DataContext = new MainViewModel();
	}

	static DiffDocumentView? View => DiffDocumentView.ActiveView;

	void OnNextHunk(object? s, RoutedEventArgs e) => View?.JumpToHunkCommand(1);
	void OnPrevHunk(object? s, RoutedEventArgs e) => View?.JumpToHunkCommand(-1);
	void OnNextFile(object? s, RoutedEventArgs e) => App.Workspace?.OpenAdjacentFileAsync(1).HandleExceptions();
	void OnPrevFile(object? s, RoutedEventArgs e) => App.Workspace?.OpenAdjacentFileAsync(-1).HandleExceptions();
	void OnToggleViewed(object? s, RoutedEventArgs e) => App.Workspace?.ToggleViewedAndAdvanceAsync().HandleExceptions();
	void OnToggleBlame(object? s, RoutedEventArgs e) => View?.ToggleBlameCommand();
	void OnCommentAtCaret(object? s, RoutedEventArgs e) => View?.CommentAtCaretCommand();
	void OnGoToDefinition(object? s, RoutedEventArgs e) => View?.GoToDefinitionCommand();
	void OnFindReferences(object? s, RoutedEventArgs e) => View?.FindReferencesCommand();
	void OnHighlightOccurrences(object? s, RoutedEventArgs e) => View?.HighlightOccurrencesCommand();
	void OnBack(object? s, RoutedEventArgs e) => App.Workspace?.GoBackAsync().HandleExceptions();
	void OnForward(object? s, RoutedEventArgs e) => App.Workspace?.GoForwardAsync().HandleExceptions();
}
