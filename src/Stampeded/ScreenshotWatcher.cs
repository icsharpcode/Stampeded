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

	/// <summary>
	/// The one pointer the harness owns. A press and the release that ends it have to arrive on
	/// the same pointer, or the release is not the other half of that gesture and nothing that
	/// tracks a press (drag distance, click count, capture) sees it.
	/// </summary>
	static readonly Avalonia.Input.Pointer TestPointer =
		new(9001, Avalonia.Input.PointerType.Mouse, isPrimary: true);

	static Avalonia.Input.KeyModifiers ParseModifiers(string text)
	{
		var modifiers = Avalonia.Input.KeyModifiers.None;
		foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
		{
			modifiers |= part.Trim().ToLowerInvariant() switch {
				"ctrl" or "control" => Avalonia.Input.KeyModifiers.Control,
				"shift" => Avalonia.Input.KeyModifiers.Shift,
				"alt" => Avalonia.Input.KeyModifiers.Alt,
				"meta" or "win" => Avalonia.Input.KeyModifiers.Meta,
				_ => Avalonia.Input.KeyModifiers.None,
			};
		}
		return modifiers;
	}

	/// <summary>
	/// Drags the pointer to a point with the button held. Selecting text takes movement, not
	/// only a press and a release: a control that follows the pointer sees nothing at all
	/// from the two ends of a gesture on their own.
	/// </summary>
	static void RaisePointerMove(Window window, Avalonia.Point point, Avalonia.Input.KeyModifiers modifiers)
	{
		var target = Avalonia.Input.InputExtensions.InputHitTest(window, point) as Interactive ?? window;
		var properties = new Avalonia.Input.PointerPointProperties(
			Avalonia.Input.RawInputModifiers.LeftMouseButton, Avalonia.Input.PointerUpdateKind.Other);
		target.RaiseEvent(new Avalonia.Input.PointerEventArgs(
			Avalonia.Input.InputElement.PointerMovedEvent, target, TestPointer, window, point,
			(ulong)Environment.TickCount64, properties, modifiers));
		CliLog.Write("action", $"move {point.X:0},{point.Y:0} {modifiers} -> {target.GetType().Name}");
	}

	/// <summary>
	/// Raises one half of a left-button gesture at a point in window coordinates, on whatever
	/// is under it - the element a real press would reach, not the focused one.
	/// </summary>
	static void RaisePointer(Window window, Avalonia.Point point, bool pressing, Avalonia.Input.KeyModifiers modifiers)
	{
		var target = Avalonia.Input.InputExtensions.InputHitTest(window, point) as Interactive ?? window;
		ulong timestamp = (ulong)Environment.TickCount64;
		var properties = new Avalonia.Input.PointerPointProperties(
			pressing ? Avalonia.Input.RawInputModifiers.LeftMouseButton : Avalonia.Input.RawInputModifiers.None,
			pressing ? Avalonia.Input.PointerUpdateKind.LeftButtonPressed : Avalonia.Input.PointerUpdateKind.LeftButtonReleased);
		if (pressing)
		{
			target.RaiseEvent(new Avalonia.Input.PointerPressedEventArgs(
				target, TestPointer, window, point, timestamp, properties, modifiers));
		}
		else
		{
			target.RaiseEvent(new Avalonia.Input.PointerReleasedEventArgs(
				target, TestPointer, window, point, timestamp, properties, modifiers,
				Avalonia.Input.MouseButton.Left));
		}
		CliLog.Write("action", $"{(pressing ? "press" : "release")} {point.X:0},{point.Y:0} "
			+ $"{modifiers} -> {target.GetType().Name}");
	}

	/// <summary>Every open window, the newest first: a dialog is what a click has to reach
	/// while it is up.</summary>
	static IEnumerable<Window> OpenWindows()
		=> (Avalonia.Application.Current?.ApplicationLifetime
			as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
			?.Windows.Reverse() ?? [];

	public static void Attach(Window mainWindow)
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
				// Whatever is in front, which is what someone looking at the screen would
				// see and what their pointer would reach. A modal dialog is its own window,
				// so capturing the main one photographed the wrong thing while a question
				// was up, and coordinates were measured against a window behind it.
				var window = OpenWindows().FirstOrDefault(w => w.IsVisible) ?? mainWindow;
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
				if (lines.Contains("caret"))
					CliLog.Write("caret", Documents.DiffDocumentView.ActiveView?.CaretDescription() ?? "(no view)");
				if (lines.Contains("stranded"))
					ReportStrandedContainers(window);
				if (lines.Contains("overview"))
					App.Workspace?.OpenOverview();
				if (lines.Contains("since-last-pass"))
					App.Workspace?.Scopes.EnterSinceLastPassAsync().HandleExceptions();
				if (lines.Contains("commit-scope"))
					App.Workspace?.Scopes.EnterCommitAsync().HandleExceptions();
				if (lines.Contains("commit-next"))
					App.Workspace?.Scopes.StepCommitAsync(1).HandleExceptions();
				if (lines.Contains("commit-exit"))
					App.Workspace?.Scopes.ExitAsync().HandleExceptions();
				if (lines.Contains("impacted"))
					App.Workspace?.Factory?.Pane<Panes.TestsPaneViewModel>("Tests")?.ApplyImpactedFilter();
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
				// menu:<header> - invokes a menu item by its header, for commands that live only
				// in the menu bar.
				foreach (var command in lines.Where(l => l.StartsWith("menu:", StringComparison.Ordinal)))
				{
					string header = command["menu:".Length..].Trim();
					// The logical tree, not the visual one: a submenu's items are not realized
					// until the menu is opened, and this has to work without opening it.
					if (Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(window).OfType<MenuItem>()
						.FirstOrDefault(m => m.Header as string == header) is { } item)
					{
						item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
					}
					else
					{
						CliLog.Write("action", $"menu: no item headed '{header}'");
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
				// press:<x>,<y>[:<modifiers>], move:<x>,<y>[:<modifiers>] and
				// release:<x>,<y>[:<modifiers>] - a pointer gesture in window coordinates, each
				// part able to carry its own modifiers, because that is the distinction a
				// gesture can get wrong: which of them the modifiers were read from. Driven in
				// one loop so the parts happen in the order they were written: a drag is a
				// press, then movement, then a release, and any other order is a click.
				foreach (var command in lines.Where(l =>
					l.StartsWith("press:", StringComparison.Ordinal)
					|| l.StartsWith("move:", StringComparison.Ordinal)
					|| l.StartsWith("release:", StringComparison.Ordinal)))
				{
					var parts = command.Split(':', 3);
					var coords = parts.Length < 2 ? [] : parts[1].Split(',');
					if (coords.Length != 2
						|| !double.TryParse(coords[0], out double x) || !double.TryParse(coords[1], out double y))
					{
						CliLog.Write("action", $"{parts[0]}: cannot read '{command}'");
						continue;
					}
					var modifiers = parts.Length == 3 ? ParseModifiers(parts[2]) : Avalonia.Input.KeyModifiers.None;
					var point = new Avalonia.Point(x, y);
					if (parts[0] == "move")
						RaisePointerMove(window, point, modifiers);
					else
						RaisePointer(window, point, parts[0] == "press", modifiers);
				}
				// wheel:<x>,<y>:<delta> - rolls the wheel over a point, positive being up. The
				// gesture some controls read instead of a click, and the only way to reach one.
				foreach (var command in lines.Where(l => l.StartsWith("wheel:", StringComparison.Ordinal)))
				{
					var parts = command.Split(':', 3);
					var coords = parts.Length < 2 ? [] : parts[1].Split(',');
					if (coords.Length != 2
						|| !double.TryParse(coords[0], out double wx) || !double.TryParse(coords[1], out double wy)
						|| !double.TryParse(parts.Length == 3 ? parts[2] : "1", out double delta))
					{
						CliLog.Write("action", $"wheel: cannot read '{command}'");
						continue;
					}
					var at = new Avalonia.Point(wx, wy);
					var rolled = Avalonia.Input.InputExtensions.InputHitTest(window, at) as Interactive ?? window;
					rolled.RaiseEvent(new Avalonia.Input.PointerWheelEventArgs(
						rolled, TestPointer, window, at, (ulong)Environment.TickCount64,
						new Avalonia.Input.PointerPointProperties(
							Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PointerUpdateKind.Other),
						Avalonia.Input.KeyModifiers.None, new Avalonia.Vector(0, delta)));
					CliLog.Write("action", $"wheel {wx:0},{wy:0} by {delta} -> {rolled.GetType().Name}");
				}
				// tooltip:<x>,<y> - shows the tooltip of whatever is under a point, so a capture
				// can hold it. Moving the pointer there does not: being hovered is state the
				// platform keeps, not an event a control can be handed.
				foreach (var command in lines.Where(l => l.StartsWith("tooltip:", StringComparison.Ordinal)))
				{
					var coords = command["tooltip:".Length..].Split(',');
					if (coords.Length != 2
						|| !double.TryParse(coords[0], out double tx) || !double.TryParse(coords[1], out double ty))
					{
						CliLog.Write("action", $"tooltip: cannot read '{command}'");
						continue;
					}
					// Up from what was hit until something carries a tip: the text of a button
					// is what a point lands on, and the tip belongs to the button.
					var hit = Avalonia.Input.InputExtensions.InputHitTest(window, new Avalonia.Point(tx, ty)) as Control;
					while (hit is not null && Avalonia.Controls.ToolTip.GetTip(hit) is null)
						hit = hit.GetVisualParent() as Control;
					if (hit is null)
					{
						CliLog.Write("action", $"tooltip {tx:0},{ty:0}: nothing there carries one");
						continue;
					}
					Avalonia.Controls.ToolTip.SetIsOpen(hit, true);
					CliLog.Write("action", $"tooltip {tx:0},{ty:0} -> {hit.GetType().Name}");
				}
				// context:<x>,<y> - asks for the context menu at a point. A synthesized right
				// button does not produce this: the request is raised for the platform's own
				// gesture, not by the control that would show the menu.
				foreach (var command in lines.Where(l => l.StartsWith("context:", StringComparison.Ordinal)))
				{
					var coords = command["context:".Length..].Split(',');
					if (coords.Length == 2
						&& double.TryParse(coords[0], out double cx) && double.TryParse(coords[1], out double cy)
						&& Avalonia.Input.InputExtensions.InputHitTest(window, new Avalonia.Point(cx, cy)) is Interactive hit)
					{
						hit.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
						CliLog.Write("action", $"context {cx:0},{cy:0} -> {hit.GetType().Name}");
					}
					else
					{
						CliLog.Write("action", $"context: nothing at '{command}'");
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
					var keyArgs = new Avalonia.Input.KeyEventArgs {
						RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
						Key = gesture.Key,
						KeyModifiers = gesture.KeyModifiers,
					};
					focused.RaiseEvent(keyArgs);
					CliLog.Write("action", $"key {gesture.KeyModifiers}+{gesture.Key} -> "
						+ $"{focused.GetType().Name}, handled={keyArgs.Handled}");
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
				// folder:<path> or folder:cancel - answers the next clone-location question,
				// which is a portal dialog no synthesized input reaches. Write it before the
				// open-url: that asks it.
				foreach (var command in lines.Where(l => l.StartsWith("folder:", StringComparison.Ordinal)))
				{
					string answer = command["folder:".Length..].Trim();
					App.NextFolderAnswer = (answer == "cancel" ? null : answer, true);
				}
				// open-url:<url or owner/repo[/pull/N]> - the Repository menu's "Open from URL".
				foreach (var command in lines.Where(l => l.StartsWith("open-url:", StringComparison.Ordinal)))
					App.OpenFromUrlAsync(command["open-url:".Length..].Trim()).HandleExceptions();
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
