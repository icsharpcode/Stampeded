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
		ScreenshotWatcher.Attach(this);
		RecentMenu.AddHandler(MenuItem.ClickEvent, OnRecentRepoClick);
	}

	void OnRecentRepoClick(object? sender, RoutedEventArgs e)
	{
		if (e.Source is MenuItem { Header: string path } item && item != RecentMenu)
			App.OpenRepositoryAsync(path).HandleExceptions();
	}

	void OnOpenRepository(object? s, RoutedEventArgs e) => PickRepositoryAsync().HandleExceptions();

	async Task PickRepositoryAsync()
	{
		var picks = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions {
			Title = "Open git repository",
			AllowMultiple = false,
		});
		if (picks.Count == 1)
			await App.OpenRepositoryAsync(picks[0].Path.LocalPath);
	}

	void OnOpenFromUrl(object? s, RoutedEventArgs e) => PromptUrlAsync().HandleExceptions();

	async Task PromptUrlAsync()
	{
		string? url = await new TextPromptWindow("Open from URL",
			"GitHub repository or pull request URL (also accepts owner/repo). A repository not cloned yet is cloned via gh into ~/Projects.",
			"Open", "https://github.com/owner/repo/pull/123").ShowDialog<string?>(this);
		if (!string.IsNullOrWhiteSpace(url))
			await App.OpenFromUrlAsync(url);
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

	// The overview page's commands, so they are reachable without going back to that tab.
	void OnEnterCommitScope(object? s, RoutedEventArgs e) => App.Workspace?.EnterCommitScopeAsync().HandleExceptions();
	void OnNextCommit(object? s, RoutedEventArgs e) => App.Workspace?.StepCommitScopeAsync(1).HandleExceptions();
	void OnPreviousCommit(object? s, RoutedEventArgs e) => App.Workspace?.StepCommitScopeAsync(-1).HandleExceptions();
	void OnExitCommitScope(object? s, RoutedEventArgs e) => App.Workspace?.ExitCommitScopeAsync().HandleExceptions();
	void OnReviewRecord(object? s, RoutedEventArgs e) => App.Workspace?.OpenReviewRecord();
	void OnBounce(object? s, RoutedEventArgs e) => App.Workspace?.PrepareBounceBody();
	void OnOpenVsCode(object? s, RoutedEventArgs e) => App.Workspace?.OpenInVsCodeAsync(oldSide: false).HandleExceptions();
	void OnOpenFixtures(object? s, RoutedEventArgs e) => App.Workspace?.OpenAffectedFixturesInILSpyAsync().HandleExceptions();

	void OnInterdiff(object? s, RoutedEventArgs e) => App.Workspace?.OpenInterdiffAsync().HandleExceptions();

	void OnOpenStart(object? s, RoutedEventArgs e) => App.Workspace?.OpenStart();

	void OnCloseReview(object? s, RoutedEventArgs e) => App.Workspace?.CloseReview();

	void OnOpenOverview(object? s, RoutedEventArgs e) => App.Workspace?.OpenOverview();

	void OnContinueFromPrepare(object? s, RoutedEventArgs e) => App.Workspace?.StartPage?.ContinueNow();

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
