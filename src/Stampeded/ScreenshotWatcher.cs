using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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
