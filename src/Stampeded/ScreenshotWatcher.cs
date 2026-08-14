using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Stampeded.Controls;
using Stampeded.Core.Infra;

namespace Stampeded;

/// <summary>
/// Renders the window to a PNG when a trigger file appears. Wayland compositors block
/// external tools from reading an XWayland window's pixels, so "take a screenshot of the
/// app" is only reliable from inside the app. Trigger: write the target PNG path into
/// <c>/tmp/stampeded-screenshot-request</c>; the file is consumed on capture.
/// </summary>
static class ScreenshotWatcher
{
	const string TriggerFile = "/tmp/stampeded-screenshot-request";

	/// <summary>
	/// Reports rows a virtualizing panel is still painting but no longer owns - the ghost rows
	/// that appear over unrelated ones. A stranded container is either absent from the panel's
	/// realized set or arranged at its own desired width, which no ordinary layout pass
	/// produces: a row always spans the panel. Both are printed with the item they still show
	/// and where they sit, which is what identifies the row that stranded them.
	/// </summary>
	static void ReportStrandedContainers(Window window)
	{
		int found = 0;
		foreach (var panel in window.GetVisualDescendants().OfType<VirtualizingStackPanel>())
		{
			if (panel.FindAncestorOfType<ItemsControl>() is not { } owner)
				continue;
			var realized = owner.GetRealizedContainers().ToHashSet();
			double width = panel.Bounds.Width;
			foreach (var child in panel.GetVisualChildren().OfType<Control>().Where(c => c.IsVisible))
			{
				bool orphaned = !realized.Contains(child);
				bool narrow = Math.Abs(child.Bounds.Width - width) > 0.5;
				if (!orphaned && !narrow)
					continue;
				found++;
				CliLog.Write("stranded",
					$"{owner.GetType().Name}/{owner.Name ?? "(unnamed)"}: '{child.DataContext}' "
					+ $"at y={child.Bounds.Y:0} w={child.Bounds.Width:0} (panel {width:0}) "
					+ $"{(orphaned ? "not realized" : "realized")}{(narrow ? ", narrow" : "")}");
			}
		}
		CliLog.Write("stranded", found == 0 ? "none" : $"{found} stranded container(s)");
	}

	/// <summary>Every open window, the newest first: a dialog is what a click has to reach
	/// while it is up.</summary>
	static IEnumerable<Window> OpenWindows()
		=> (Avalonia.Application.Current?.ApplicationLifetime
			as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
			?.Windows.Reverse() ?? [];

	public static void Attach(Window window)
	{
		var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		timer.Tick += (_, _) => {
			if (!File.Exists(TriggerFile))
				return;
			try
			{
				var lines = File.ReadAllLines(TriggerFile);
				File.Delete(TriggerFile);
				string target = lines.Length > 0 ? lines[0].Trim() : "";
				if (target.Length == 0)
					return;
				// Optional extra lines are debug commands executed before the capture,
				// so interactive UI (e.g. the inline comment editor) can be verified.
				if (lines.Contains("comment"))
					Documents.DiffDocumentView.ActiveView?.CommentAtCaretCommand();
				if (lines.Contains("close-review"))
					App.Workspace?.CloseReviewAsync().HandleExceptions();
				if (lines.Contains("callgraph") && Documents.DiffDocumentView.ActiveView is { } active)
					active.ShowCallGraphCommand();
				// open-range:<base>:<head> - opens a local review, for testing without a PR.
				foreach (var command in lines.Where(l => l.StartsWith("open-range:", StringComparison.Ordinal)))
				{
					var parts = command.Split(':', 3);
					if (parts.Length == 3)
						App.Workspace?.OpenLocalRangeAsync(parts[1].Trim(), parts[2].Trim()).HandleExceptions();
				}
				foreach (var command in lines.Where(l => l.StartsWith("highlight:", StringComparison.Ordinal)))
				{
					var parts = command.Split(':', 3);
					if (parts.Length == 3 && int.TryParse(parts[1], out int hl) && int.TryParse(parts[2], out int col)
						&& Documents.DiffDocumentView.ActiveView is { } view)
					{
						view.HighlightAtCommand(hl, col);
					}
				}
				foreach (var command in lines.Where(l => l.StartsWith("expand:", StringComparison.Ordinal)))
				{
					var parts = command.Split(':', 3);
					if (parts.Length == 3 && int.TryParse(parts[2], out int row)
						&& window.GetVisualDescendants().OfType<Controls.TreeView.SharpTreeView>()
							.FirstOrDefault(t => t.Name == parts[1].Trim()) is { } tree
						&& tree.ItemsSource is System.Collections.IList flat && row < flat.Count
						&& flat[row] is Core.TreeView.SharpTreeNode node)
					{
						node.IsExpanded = true;
					}
				}
				if (lines.Contains("changed-only"))
				{
					if (window.GetVisualDescendants().OfType<CheckBox>()
						.FirstOrDefault(c => c.Content as string == "Only members this review changes") is { } box)
					{
						box.IsChecked = box.IsChecked != true;
					}
				}
				if (lines.Contains("stranded"))
					ReportStrandedContainers(window);
				if (lines.Contains("overview"))
					App.Workspace?.OpenOverview();
				if (lines.Contains("commit-scope"))
					App.Workspace?.EnterCommitScopeAsync().HandleExceptions();
				if (lines.Contains("commit-next"))
					App.Workspace?.StepCommitScopeAsync(1).HandleExceptions();
				if (lines.Contains("commit-exit"))
					App.Workspace?.ExitCommitScopeAsync().HandleExceptions();
				if (lines.Contains("sbs"))
					App.Workspace?.OpenSideBySideAsync().HandleExceptions();
				if (lines.Contains("vscode"))
					App.Workspace?.OpenInVsCodeAsync(oldSide: false).HandleExceptions();
				if (lines.Contains("ilspy-fixtures"))
					App.Workspace?.OpenAffectedFixturesInILSpyAsync().HandleExceptions();
				foreach (var pane in lines.Where(l => l.StartsWith("pane:", StringComparison.Ordinal)))
					App.Workspace?.Factory?.ShowPane(pane["pane:".Length..].Trim());
				// check:<control-name> - checks a named toggle/radio, so mode-dependent UI
				// can be captured.
				foreach (var command in lines.Where(l => l.StartsWith("check:", StringComparison.Ordinal)))
				{
					if (window.GetVisualDescendants().OfType<ToggleButton>()
						.FirstOrDefault(t => t.Name == command["check:".Length..].Trim()) is { } toggle)
					{
						toggle.IsChecked = true;
					}
				}
				// click:<name or label> - presses a button, for commands that have no gesture to
				// raise and no view model the watcher can reach. The name wins over the label,
				// which several buttons share ("Refresh" labels six of them).
				foreach (var command in lines.Where(l => l.StartsWith("click:", StringComparison.Ordinal)))
				{
					string label = command["click:".Length..].Trim();
					// Only what is on screen: every document's view stays attached, so an
					// off-screen tab's button would otherwise be pressed instead of the one
					// the capture shows. Dialogs are their own windows, and a modal one is the
					// only thing that can be pressed while it is up - so it is searched first.
					var buttons = OpenWindows()
						.SelectMany(w => w.GetVisualDescendants().OfType<Button>())
						.Where(b => b.IsEffectivelyVisible)
						.ToList();
					if ((buttons.FirstOrDefault(b => b.Name == label)
						?? buttons.FirstOrDefault(b => b.Content as string == label)) is { } button)
					{
						button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
					}
					else
					{
						CliLog.Write("action", $"click: no button labelled '{label}'");
					}
				}
				// type:<text> - types into whatever holds focus, for flows that only exist once
				// there is text (a draft comment, a filter, a branch name).
				foreach (var command in lines.Where(l => l.StartsWith("type:", StringComparison.Ordinal)))
				{
					if (window.FocusManager?.GetFocusedElement() is Avalonia.Interactivity.Interactive focusedInput)
					{
						focusedInput.RaiseEvent(new Avalonia.Input.TextInputEventArgs {
							RoutedEvent = Avalonia.Input.InputElement.TextInputEvent,
							Text = command["type:".Length..],
						});
					}
				}
				// key:<gesture> - raises a key press on the window (e.g. "key:Ctrl+OemPlus"),
				// so a gesture handler is exercised rather than the state it produces.
				foreach (var command in lines.Where(l => l.StartsWith("key:", StringComparison.Ordinal)))
				{
					var gesture = Avalonia.Input.KeyGesture.Parse(command["key:".Length..].Trim());
					// On the focused element, as a real key press arrives: a gesture handled by
					// whatever holds focus is exactly what a window-level raise would miss.
					var focused = window.FocusManager?.GetFocusedElement() as Avalonia.Interactivity.Interactive
						?? window;
					focused.RaiseEvent(new Avalonia.Input.KeyEventArgs {
						RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
						Key = gesture.Key,
						KeyModifiers = gesture.KeyModifiers,
					});
				}
				// select:<list-name>:<index> - drives a named ListBox's selection, so
				// selection-dependent UI can be captured.
				foreach (var command in lines.Where(l => l.StartsWith("select:", StringComparison.Ordinal)))
				{
					var parts = command.Split(':', 3);
					// A UserControl keeps its own name scope, so the window cannot resolve
					// these names directly; walk the visual tree instead.
					if (parts.Length == 3 && int.TryParse(parts[2], out int index)
						&& window.GetVisualDescendants().OfType<ListBox>()
							.FirstOrDefault(l => l.Name == parts[1].Trim()) is { } list
						&& index < list.ItemCount)
					{
						list.SelectedIndex = index;
						list.ScrollRowIntoView(index);
					}
				}
				// open-file:<repo-relative path> - opens a file's diff without navigating into
				// it, so what a reader first sees can be captured.
				foreach (var command in lines.Where(l => l.StartsWith("open-file:", StringComparison.Ordinal)))
				{
					string rel = command["open-file:".Length..].Trim();
					if (App.Workspace is { } ws
						&& ws.Files.FirstOrDefault(f => f.Path == rel) is { } file)
					{
						ws.OpenFileAsync(file).HandleExceptions();
					}
				}
				foreach (var command in lines.Where(l => l.StartsWith("goto:", StringComparison.Ordinal)))
				{
					var parts = command.Split(':', 3);
					if (parts.Length == 3 && int.TryParse(parts[2], out int gotoLine))
						App.Workspace?.NavigateToFileLineAsync(parts[1], gotoLine, oldSide: false, record: false).HandleExceptions();
				}
				var size = new Avalonia.PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height);
				using var bitmap = new RenderTargetBitmap(size, new Avalonia.Vector(96, 96));
				bitmap.Render(window);
#pragma warning disable CS0618 // default PNG encoding is all this debug utility needs
				bitmap.Save(target);
#pragma warning restore CS0618
				CliLog.Write("action", $"screenshot -> {target}");
			}
			catch (Exception ex)
			{
				CliLog.Write("action", $"screenshot FAILED: {ex.Message}");
			}
		};
		timer.Start();
	}
}
