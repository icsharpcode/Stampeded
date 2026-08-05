using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
					App.Workspace?.CloseReview();
				if (lines.Contains("callgraph") && Documents.DiffDocumentView.ActiveView is { } active)
					active.ShowCallGraphCommand();
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
