using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

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

	MainViewModel? Vm => DataContext as MainViewModel;

	void OnZoomIn(object? s, RoutedEventArgs e) => Vm?.ZoomIn();

	void OnZoomOut(object? s, RoutedEventArgs e) => Vm?.ZoomOut();

	void OnZoomReset(object? s, RoutedEventArgs e) => Vm?.ZoomReset();

	/// <summary>
	/// The zoom gestures. They are handled here rather than as InputGesture on the menu items
	/// so that a "+" typed into a text box stays a "+"; the menu headers carry the gestures for
	/// discoverability only. Both halves of the keyboard reach them: Ctrl+"+" arrives as
	/// OemPlus on the main block, needing no Shift to match, and as Add on the numpad.
	/// </summary>
	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.Handled)
			return;
		// A review gesture is a letter to anyone typing one: whatever was aimed at a text box
		// stays in it. Everything else in the window is a list, a tree or a read-only diff,
		// where a letter has no meaning of its own worth keeping.
		if (e.Source is Avalonia.Visual source
			&& source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
		{
			return;
		}
		// The keys that drive a review, wherever focus happens to be. Reading is not only done
		// in the diff: the reader is as often in the Explorer, the Commits pane or the Tests
		// pane when they decide to mark a file viewed or step to the next hunk, and a gesture
		// that works in one pane and dies in the next is worse than no gesture.
		//
		// The diff view handles the same keys on the way down while it has focus, so this only
		// ever runs on what came back unhandled - the caret-dependent ones still act on the
		// document in front, through its view.
		switch ((e.Key, e.KeyModifiers))
		{
			case (Key.N, KeyModifiers.None):
			case (Key.Down, KeyModifiers.Control):
				View?.JumpToHunkCommand(1);
				break;
			case (Key.P, KeyModifiers.None):
			case (Key.Up, KeyModifiers.Control):
				View?.JumpToHunkCommand(-1);
				break;
			case (Key.OemCloseBrackets, KeyModifiers.None):
				App.Workspace?.OpenAdjacentFileAsync(1).HandleExceptions();
				break;
			case (Key.OemOpenBrackets, KeyModifiers.None):
				App.Workspace?.OpenAdjacentFileAsync(-1).HandleExceptions();
				break;
			case (Key.OemCloseBrackets, KeyModifiers.Control):
				App.Workspace?.StepCommitScopeAsync(1).HandleExceptions();
				break;
			case (Key.OemOpenBrackets, KeyModifiers.Control):
				App.Workspace?.StepCommitScopeAsync(-1).HandleExceptions();
				break;
			case (Key.V, KeyModifiers.None):
				App.Workspace?.ToggleViewedAndAdvanceAsync().HandleExceptions();
				break;
			case (Key.O, KeyModifiers.None):
				App.Workspace?.ToggleOverviewAsync().HandleExceptions();
				break;
			case (Key.U, KeyModifiers.None):
				View?.JumpToUncoveredCommand();
				break;
			case (Key.B, KeyModifiers.None):
				View?.ToggleBlameCommand();
				break;
			case (Key.C, KeyModifiers.None):
				View?.CommentAtCaretCommand();
				break;
			case (Key.F12, KeyModifiers.None):
				View?.GoToDefinitionCommand();
				break;
			case (Key.F12, KeyModifiers.Shift):
				View?.FindReferencesCommand();
				break;
			case (Key.Left, KeyModifiers.Alt):
				App.Workspace?.GoBackAsync().HandleExceptions();
				break;
			case (Key.Right, KeyModifiers.Alt):
				App.Workspace?.GoForwardAsync().HandleExceptions();
				break;
			case (Key.W, KeyModifiers.Control):
				App.Workspace?.CloseActiveDocument();
				break;
			case (Key.OemPlus or Key.Add, KeyModifiers.Control):
				Vm?.ZoomIn();
				break;
			case (Key.OemMinus or Key.Subtract, KeyModifiers.Control):
				Vm?.ZoomOut();
				break;
			case (Key.D0 or Key.NumPad0, KeyModifiers.Control):
				Vm?.ZoomReset();
				break;
			default:
				return;
		}
		e.Handled = true;
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
	void OnToggleOverview(object? s, RoutedEventArgs e) => App.Workspace?.ToggleOverviewAsync().HandleExceptions();
	void OnToggleBlame(object? s, RoutedEventArgs e) => View?.ToggleBlameCommand();
	void OnCommentAtCaret(object? s, RoutedEventArgs e) => View?.CommentAtCaretCommand();
	void OnGoToDefinition(object? s, RoutedEventArgs e) => View?.GoToDefinitionCommand();
	void OnFindReferences(object? s, RoutedEventArgs e) => View?.FindReferencesCommand();
	void OnHighlightOccurrences(object? s, RoutedEventArgs e) => View?.HighlightOccurrencesCommand();
	void OnSideBySide(object? s, RoutedEventArgs e) => App.Workspace?.OpenSideBySideAsync().HandleExceptions();
	void OnCloseDocument(object? s, RoutedEventArgs e) => App.Workspace?.CloseActiveDocument();

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

	void OnCloseReview(object? s, RoutedEventArgs e) => App.Workspace?.CloseReviewAsync().HandleExceptions();

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
