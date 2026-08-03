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
	void OnSideBySide(object? s, RoutedEventArgs e) => App.Workspace?.OpenSideBySideAsync().HandleExceptions();

	void OnShowPane(object? s, RoutedEventArgs e)
	{
		if (s is MenuItem { Tag: string id })
			App.Workspace?.Factory?.ShowPane(id);
	}
	void OnPruneWorktrees(object? s, RoutedEventArgs e) => App.Workspace?.PruneWorktreeCacheAsync().HandleExceptions();

	void OnRebasePr(object? s, RoutedEventArgs e) => App.Workspace?.RebaseCurrentPrOnTargetAsync().HandleExceptions();

	void OnOpenOnGitHub(object? s, RoutedEventArgs e)
	{
		if (App.Workspace is { CurrentPr: { } pr } ws)
			ws.OpenOnGitHubAsync(pr.Number).HandleExceptions();
	}

	void OnShowSemanticLog(object? s, RoutedEventArgs e)
	{
		if (App.Workspace is not { } ws)
			return;
		string log = $"== head workspace ({ws.Semantics?.State.ToString() ?? "none"}) ==\n{ws.Semantics?.LoadLog}\n" +
			$"== base workspace ({ws.BaseSemantics?.State.ToString() ?? "none"}) ==\n{ws.BaseSemantics?.LoadLog}";
		ws.OpenTextDocument("semlog", "Semantic load log", log);
	}
	void OnBack(object? s, RoutedEventArgs e) => App.Workspace?.GoBackAsync().HandleExceptions();
	void OnForward(object? s, RoutedEventArgs e) => App.Workspace?.GoForwardAsync().HandleExceptions();
}
