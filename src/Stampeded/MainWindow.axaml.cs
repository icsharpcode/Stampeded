using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using Stampeded.Documents;

namespace Stampeded;

public partial class MainWindow : Window
{
	// The menu items this file has to reach: the ones whose state is not a binding away, and the
	// two whose submenu is built when it opens. A menu item is a model object rather than a
	// control, so it cannot carry an x:Name and Avalonia generates no field for one - what names
	// them instead is the key each carries as its CommandParameter.
	readonly NativeMenuItem recentMenu, buildSolutionMenu, exitItem,
		nextHunkItem, prevHunkItem, nextUncoveredItem, historyOfSelectionItem,
		backItem, forwardItem,
		sideBySideItem, blameItem, multiRowTabsItem, pointerCrossHairItem, debugHereItem;

	public MainWindow()
	{
		InitializeComponent();
		var menu = NativeMenu.GetMenu(this);
		NativeMenuItem Named(string key) => FindMenuItem(menu, i => key.Equals(i.CommandParameter))
			?? throw new InvalidOperationException($"no menu item is keyed {key}");
		recentMenu = Named("RecentMenu");
		buildSolutionMenu = Named("BuildSolutionMenu");
		exitItem = Named("ExitItem");
		nextHunkItem = Named("NextHunkItem");
		prevHunkItem = Named("PrevHunkItem");
		nextUncoveredItem = Named("NextUncoveredItem");
		historyOfSelectionItem = Named("HistoryOfSelectionItem");
		backItem = Named("BackItem");
		forwardItem = Named("ForwardItem");
		sideBySideItem = Named("SideBySideItem");
		blameItem = Named("BlameItem");
		multiRowTabsItem = Named("MultiRowTabsItem");
		pointerCrossHairItem = Named("PointerCrossHairItem");
		debugHereItem = Named("DebugHereItem");
		WindowPlacement.Attach(this);
		DataContext = new MainViewModel();
		ScreenshotWatcher.Attach(this);
		// A menu about to be shown is the only moment its per-document state is not stale, and
		// each platform says so differently: the exported macOS menu asks the model for an
		// update, while the bar drawn inside the window raises a routed event from the item that
		// opened. Both lead to the same refresh.
		foreach (var top in menu!.Items.OfType<NativeMenuItem>())
			top.Menu!.NeedsUpdate += OnMenuOpening;
		AddHandler(MenuItem.SubmenuOpenedEvent, OnSubmenuOpened, RoutingStrategies.Bubble);
		// The extra mouse buttons, the way every browser and IDE reads them. The press is
		// taken so nothing under the pointer acts on it, and the release does the navigating -
		// handledEventsToo because the editors handle pointer events for their own gestures
		// without ever using these two buttons.
		AddHandler(PointerPressedEvent, OnNavigationPointerPressed, RoutingStrategies.Tunnel);
		AddHandler(PointerReleasedEvent, OnNavigationPointerReleased,
			RoutingStrategies.Bubble, handledEventsToo: true);
		// Quitting is the app menu's job on macOS, where every application has that item in the
		// same place; a second one under Review would be the odd one out. Taken out of the model
		// rather than hidden, so the separator that introduced it goes with it instead of ending
		// the menu on a rule with nothing under it.
		if (OperatingSystem.IsMacOS() && exitItem.Parent is { } review)
		{
			int index = review.Items.IndexOf(exitItem);
			review.Items.RemoveAt(index);
			if (index > 0 && review.Items[index - 1] is NativeMenuItemSeparator separator)
				review.Items.Remove(separator);
		}
	}

	void OnNavigationPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (e.GetCurrentPoint(this).Properties.PointerUpdateKind
			is PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)
		{
			e.Handled = true;
		}
	}

	void OnNavigationPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (App.Workspace is not { } workspace)
			return;
		switch (e.InitialPressMouseButton)
		{
			case MouseButton.XButton1:
				workspace.GoBackAsync().HandleExceptions();
				break;
			case MouseButton.XButton2:
				workspace.GoForwardAsync().HandleExceptions();
				break;
			default:
				return;
		}
		e.Handled = true;
	}

	/// <summary>The in-window menu bar's stand-in for a menu that is about to be shown. Only the
	/// top row counts: the submenus this rebuilds are nested ones, and rebuilding a submenu while
	/// it is opening takes away what the pointer is already over.</summary>
	void OnSubmenuOpened(object? s, RoutedEventArgs e)
	{
		if (e.Source is MenuItem { Parent: Menu })
			OnMenuOpening(s, e);
	}

	/// <summary>The first item anywhere under a menu that answers to the predicate: how an item is
	/// picked out of a menu that has no controls in it to look up.</summary>
	internal static NativeMenuItem? FindMenuItem(NativeMenu? menu, Func<NativeMenuItem, bool> match)
	{
		foreach (var item in menu?.Items.OfType<NativeMenuItem>() ?? Enumerable.Empty<NativeMenuItem>())
		{
			if (match(item))
				return item;
			if (FindMenuItem(item.Menu, match) is { } nested)
				return nested;
		}
		return null;
	}

	MainViewModel? Vm => DataContext as MainViewModel;

	void OnZoomIn(object? s, EventArgs e) => Vm?.ZoomIn();

	void OnZoomOut(object? s, EventArgs e) => Vm?.ZoomOut();

	void OnZoomReset(object? s, EventArgs e) => Vm?.ZoomReset();

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
				App.Workspace?.Scopes.StepCommitAsync(1).HandleExceptions();
				break;
			case (Key.OemOpenBrackets, KeyModifiers.Control):
				App.Workspace?.Scopes.StepCommitAsync(-1).HandleExceptions();
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
			case (Key.F5, KeyModifiers.None):
				App.Workspace?.ReloadReviewAsync().HandleExceptions();
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
			case (Key.G, KeyModifiers.Control):
				GoToAsync().HandleExceptions();
				break;
			default:
				return;
		}
		e.Handled = true;
	}

	void OnOpenRepository(object? s, EventArgs e) => PickRepositoryAsync().HandleExceptions();

	async Task PickRepositoryAsync()
	{
		var picks = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions {
			Title = "Open git repository",
			AllowMultiple = false,
		});
		if (picks.Count == 1)
			await App.OpenRepositoryAsync(picks[0].Path.LocalPath);
	}

	void OnGoTo(object? s, EventArgs e) => GoToAsync().HandleExceptions();

	/// <summary>The one dialog for both halves of "go to": which file, and where in it. A file
	/// without a line opens it where it was left; a line without a file moves within the
	/// document in front, which is the only thing a bare number can mean.</summary>
	async Task GoToAsync()
	{
		if (App.Workspace is not { } workspace)
			return;
		var target = await new GoToWindow(workspace, workspace.CurrentFile?.Path).ShowDialog<GoToTarget?>(this);
		if (target is null)
			return;
		string? path = target.Path ?? workspace.CurrentFile?.Path;
		if (path is null)
			return;
		// Everything goes through the one navigation: it opens a file of the change as its
		// diff, anything else as a source view read from the head revision, reveals a line
		// hidden in a context gap, and records the jump for Alt+Left.
		await workspace.NavigateToFileLineAsync(path, target.Line ?? 1, oldSide: false, record: true);
	}

	void OnOpenFromUrl(object? s, EventArgs e) => PromptUrlAsync().HandleExceptions();

	async Task PromptUrlAsync()
	{
		string? url = await new TextPromptWindow("Open from URL",
			"GitHub repository or pull request URL (also accepts owner/repo). A repository not cloned yet is cloned via gh into ~/Projects.",
			"Open", "https://github.com/owner/repo/pull/123").ShowDialog<string?>(this);
		if (!string.IsNullOrWhiteSpace(url))
			await App.OpenFromUrlAsync(url);
	}

	/// <summary>The view of the document in front. Commands go there rather than to whichever
	/// diff was focused last: with a side-by-side tab in front, that used to be a unified
	/// document behind it, and pressing a key acted on what the reader could not see.</summary>
	static IReviewDocumentView? View => ReviewViews.Active;

	/// <summary>Whether the document in front carries out a command, for the menu to grey out
	/// what this layout has not got.</summary>
	static bool Has(ReviewCommands command) => View?.Supported.HasFlag(command) == true;

	void OnNextHunk(object? s, EventArgs e) => View?.JumpToHunkCommand(1);
	void OnPrevHunk(object? s, EventArgs e) => View?.JumpToHunkCommand(-1);
	void OnNextFile(object? s, EventArgs e) => App.Workspace?.OpenAdjacentFileAsync(1).HandleExceptions();
	void OnPrevFile(object? s, EventArgs e) => App.Workspace?.OpenAdjacentFileAsync(-1).HandleExceptions();
	void OnToggleViewed(object? s, EventArgs e) => App.Workspace?.ToggleViewedAndAdvanceAsync().HandleExceptions();
	void OnToggleOverview(object? s, EventArgs e) => App.Workspace?.ToggleOverviewAsync().HandleExceptions();
	void OnToggleBlame(object? s, EventArgs e) => View?.ToggleBlameCommand();
	void OnCommentAtCaret(object? s, EventArgs e) => View?.CommentAtCaretCommand();
	void OnGoToDefinition(object? s, EventArgs e) => View?.GoToDefinitionCommand();
	void OnFindReferences(object? s, EventArgs e) => View?.FindReferencesCommand();
	void OnHighlightOccurrences(object? s, EventArgs e) => View?.HighlightOccurrencesCommand();
	void OnSideBySide(object? s, EventArgs e)
		=> App.Workspace?.SetDiffLayoutAsync(!DiffLayoutPreference.SideBySide).HandleExceptions();
	void OnCloseDocument(object? s, EventArgs e) => App.Workspace?.CloseActiveDocument();

	void OnShowPane(object? s, EventArgs e)
	{
		if (s is NativeMenuItem { CommandParameter: string id })
			App.Workspace?.Factory?.ShowPane(id);
	}
	void OnPruneWorktrees(object? s, EventArgs e) => App.Workspace?.PruneWorktreeCacheAsync().HandleExceptions();

	/// <summary>The repositories opened before. A menu item has no ItemsSource to bind the list
	/// to, so it is built when the menu opens - which is also the moment it can have grown since
	/// the window was created.</summary>
	void FillRecentMenu()
	{
		var items = recentMenu.Menu!.Items;
		items.Clear();
		foreach (string path in Vm?.Recent ?? Enumerable.Empty<string>())
		{
			var item = new NativeMenuItem { Header = path };
			item.Click += (_, _) => App.OpenRepositoryAsync(path).HandleExceptions();
			items.Add(item);
		}
	}

	/// <summary>
	/// Lists the checkout's solutions so one can be named for builds, with what the automatic
	/// choice would pick written beside it. Built when the menu opens: which solutions a
	/// repository has is a fact about the repository in front, and the window outlives several.
	/// </summary>
	void FillBuildSolutionMenu()
	{
		var items = buildSolutionMenu.Menu!.Items;
		items.Clear();
		if (App.Workspace is not { } workspace)
			return;
		string root = workspace.RepoPath;
		string? chosen = BuildSolutionPreference.For(root);
		string automatic = Core.Infra.SolutionTarget.ForRoot(root) ?? "nothing to build";
		items.Add(Option($"Automatic  ({automatic})", null, chosen is null));
		foreach (string solution in Core.Infra.SolutionTarget.Candidates(root))
			items.Add(Option(solution, solution, solution == chosen));

		NativeMenuItem Option(string header, string? solution, bool current)
		{
			var item = new NativeMenuItem {
				Header = header,
				ToggleType = MenuItemToggleType.Radio,
				IsChecked = current,
			};
			item.Click += (_, _) => {
				if (BuildSolutionPreference.For(root) == solution)
					return;
				BuildSolutionPreference.Set(root, solution);
				Core.Infra.CliLog.Write("action",
					$"solution to build: {solution ?? "automatic"} ({Path.GetFileName(root)})");
				// The compilation behind every semantic answer came from the solution that was
				// set before, so the choice is only made when it is loaded again.
				workspace.ReloadSemantics();
			};
			return item;
		}
	}

	void OnRebasePr(object? s, EventArgs e) => App.Workspace?.RebaseCurrentPrOnTargetAsync().HandleExceptions();

	// The overview page's commands, so they are reachable without going back to that tab.
	void OnEnterCommitScope(object? s, EventArgs e) => App.Workspace?.Scopes.EnterCommitAsync().HandleExceptions();
	void OnNextCommit(object? s, EventArgs e) => App.Workspace?.Scopes.StepCommitAsync(1).HandleExceptions();
	void OnPreviousCommit(object? s, EventArgs e) => App.Workspace?.Scopes.StepCommitAsync(-1).HandleExceptions();
	void OnExitCommitScope(object? s, EventArgs e) => App.Workspace?.Scopes.ExitAsync().HandleExceptions();
	void OnOpenVsCode(object? s, EventArgs e) => App.Workspace?.OpenInVsCodeAsync(oldSide: false).HandleExceptions();
	void OnOpenFixtures(object? s, EventArgs e) => App.Workspace?.OpenAffectedFixturesInILSpyAsync().HandleExceptions();

	void OnSinceLastPass(object? s, EventArgs e) => App.Workspace?.Scopes.EnterSinceLastPassAsync().HandleExceptions();

	// Choosing where a pass starts also reads from there: the choice is only ever made to see
	// that scope, and leaving the reader to press the scope entry afterwards is a second step
	// for one intention.
	void OnPassFromMarked(object? s, EventArgs e) => UsePassBaseline(PassBaselineKind.MarkedViewed);
	void OnPassFromSubmitted(object? s, EventArgs e) => UsePassBaseline(PassBaselineKind.SubmittedReview);
	void OnPassFromOpened(object? s, EventArgs e) => UsePassBaseline(PassBaselineKind.Opened);

	static void UsePassBaseline(PassBaselineKind kind)
	{
		if (App.Workspace is not { } workspace)
			return;
		workspace.Scopes.UsePassBaseline(kind);
		workspace.Scopes.EnterSinceLastPassAsync().HandleExceptions();
	}

	void OnOpenStart(object? s, EventArgs e) => App.Workspace?.OpenStart();

	void OnCloseReview(object? s, EventArgs e) => App.Workspace?.CloseReviewAsync().HandleExceptions();

	void OnReloadReview(object? s, EventArgs e) => App.Workspace?.ReloadReviewAsync().HandleExceptions();

	void OnOpenOverview(object? s, EventArgs e) => App.Workspace?.OpenOverview();

	void OnContinueFromPrepare(object? s, RoutedEventArgs e) => App.Workspace?.StartPage?.ContinueNow();

	void OnOpenOnGitHub(object? s, EventArgs e)
	{
		if (App.Workspace is { CurrentPr: { } pr } ws)
			ws.OpenOnGitHubAsync(pr.Number).HandleExceptions();
	}

	void OnShowSemanticLog(object? s, EventArgs e)
	{
		if (App.Workspace is not { } ws)
			return;
		string log = $"== head workspace ({ws.Semantics?.State.ToString() ?? "none"}) ==\n{ws.Semantics?.LoadLog}\n" +
			$"== base workspace ({ws.BaseSemantics?.State.ToString() ?? "none"}) ==\n{ws.BaseSemantics?.LoadLog}";
		ws.OpenTextDocument("semlog", "Semantic load log", log);
	}
	void OnBack(object? s, EventArgs e) => App.Workspace?.GoBackAsync().HandleExceptions();
	void OnForward(object? s, EventArgs e) => App.Workspace?.GoForwardAsync().HandleExceptions();

	void OnNextUncovered(object? s, EventArgs e) => View?.JumpToUncoveredCommand();

	void OnCallGraph(object? s, EventArgs e) => View?.ShowCallGraphCommand();

	void OnHistoryOfSelection(object? s, EventArgs e) => View?.HistoryOfSelectionCommand();

	void OnDebugHere(object? s, EventArgs e) => View?.DebugHereCommand();

	void OnOpenReviewDocument(object? s, EventArgs e) => App.Workspace?.OpenReviewDocument();

	void OnExit(object? s, EventArgs e) => Close();

	void OnToggleMultiRowTabs(object? s, EventArgs e) => TabRowsPreference.Set(!TabRowsPreference.MultiRow);

	/// <summary>Switches the pointer cross-hair on every open view. A debug build only: what it
	/// draws is a developer's answer to "where does the app think the pointer is", which is the
	/// first question whenever a tooltip or popup opens somewhere unexpected.</summary>
	void OnTogglePointerCrossHair(object? s, EventArgs e)
	{
#if DEBUG
		Editor.PointerCrossHairRenderer.IsEnabled = !Editor.PointerCrossHairRenderer.IsEnabled;
		Core.Infra.CliLog.Write("action",
			$"pointer cross-hair: {(Editor.PointerCrossHairRenderer.IsEnabled ? "on" : "off")}");
#endif
	}

	void OnKeyboardShortcuts(object? s, EventArgs e)
		=> App.Workspace?.OpenTextDocument("keys", "Keyboard shortcuts", KeyboardShortcuts.Text);

	// The pane commands: the pane comes forward first, because a run whose output lands behind
	// whichever pane happens to be in front is a run nobody sees.
	void OnRunTests(object? s, EventArgs e) => Pane<Panes.TestsPaneViewModel>("Tests")?.Run();

	void OnRunTestsWithCoverage(object? s, EventArgs e)
		=> Pane<Panes.TestsPaneViewModel>("Tests")?.RunWithCoverage();

	void OnRunTestsAB(object? s, EventArgs e) => Pane<Panes.TestsPaneViewModel>("Tests")?.RunAB();

	void OnImpactedTestFilter(object? s, EventArgs e)
		=> Pane<Panes.TestsPaneViewModel>("Tests")?.ApplyImpactedFilter();

	void OnRunApplication(object? s, EventArgs e) => Pane<Panes.RunPaneViewModel>("Run")?.Run();

	void OnRefreshChecks(object? s, EventArgs e)
	{
		App.Workspace?.Factory?.ShowPane("Checks");
		App.Workspace?.RequestChecksRefresh();
	}

	static T? Pane<T>(string id) where T : Dock.Model.Mvvm.Controls.Tool
	{
		if (App.Workspace?.Factory is not { } factory)
			return null;
		factory.ShowPane(id);
		return factory.Pane<T>(id);
	}

	void OnMenuOpening(object? s, EventArgs e) => RefreshMenus();

	/// <summary>
	/// What the menu can offer that depends on the tab in front. The review's own state is
	/// bound - it has events to change on - but which document is active, whether its blame
	/// margin is showing and how deeply its file is meant to be read have none. Read when the
	/// menu opens, which is the only moment it matters and the only one that cannot be stale -
	/// and by the screenshot watcher, which invokes an item straight out of the model without
	/// opening anything, so nothing else would have filled the submenus it looks in.
	/// </summary>
	internal void RefreshMenus()
	{
		var view = View;
		var file = App.Workspace?.CurrentFile;
		multiRowTabsItem.IsChecked = TabRowsPreference.MultiRow;
#if DEBUG
		pointerCrossHairItem.IsVisible = true;
		pointerCrossHairItem.IsChecked = Editor.PointerCrossHairRenderer.IsEnabled;
#endif
		FillRecentMenu();
		FillBuildSolutionMenu();
		backItem.IsEnabled = App.Workspace?.CanGoBack ?? false;
		forwardItem.IsEnabled = App.Workspace?.CanGoForward ?? false;
		nextHunkItem.IsEnabled = Has(ReviewCommands.JumpToHunk);
		prevHunkItem.IsEnabled = Has(ReviewCommands.JumpToHunk);
		nextUncoveredItem.IsEnabled = Has(ReviewCommands.JumpToUncovered);
		sideBySideItem.IsChecked = DiffLayoutPreference.SideBySide;
		blameItem.IsEnabled = Has(ReviewCommands.ToggleBlame);
		blameItem.IsChecked = view?.BlameVisible ?? false;
		debugHereItem.IsEnabled = Has(ReviewCommands.DebugHere);
		historyOfSelectionItem.IsEnabled = Has(ReviewCommands.HistoryOfSelection);
	}
}
