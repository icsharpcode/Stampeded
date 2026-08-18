using Avalonia;
using Avalonia.Controls;

namespace Stampeded;

/// <summary>
/// Where the window was and how it stood, kept between sessions.
///
/// What is remembered is the geometry the window has when it is neither maximized nor
/// full-screen, plus the state itself: un-maximizing a restored window has to land somewhere,
/// and the size a maximized window reports is the screen's, not the reader's choice.
///
/// A saved position is a claim about a screen that may no longer be there - an external
/// display unplugged, a resolution changed - so it is checked against the screens that exist
/// now before it is applied. A window nobody can see is worse than a window in the default
/// place, and it cannot be dragged back into view.
/// </summary>
static class WindowPlacement
{
	const string FileName = "window.txt";

	/// <summary>How much of the window's top-left corner has to land on a screen for the
	/// position to be worth restoring: enough of the title bar to grab with the pointer.</summary>
	const int VisibleCorner = 80;

	const double MinimumSize = 400;

	public static void Attach(Window window)
	{
		var (geometry, maximized) = Restore(window);
		if (maximized)
		{
			// On opening rather than now, and letting go of the explicit size as it happens: a
			// window told how big to be keeps that size even once the window manager maximizes
			// it, so it came up maximized in name and in its old size on screen. The size it
			// was given before opening is the one it goes back to when un-maximized.
			window.Opened += (_, _) => {
				window.WindowState = WindowState.Maximized;
				window.Width = double.NaN;
				window.Height = double.NaN;
			};
		}
		window.PositionChanged += (_, _) => Remember();
		window.PropertyChanged += (_, e) => {
			if (e.Property == Window.ClientSizeProperty || e.Property == Window.WindowStateProperty)
				Remember();
		};
		window.Closing += (_, _) => Save(geometry, window.WindowState);

		// Only while the window stands on its own: maximized, its position and size are the
		// screen's answer rather than the reader's, and restoring those would lose the size the
		// window goes back to.
		void Remember()
		{
			if (window.WindowState == WindowState.Normal)
				geometry = new PixelRect(window.Position, PixelSize.FromSize(window.ClientSize, window.DesktopScaling));
		}
	}

	static void Save(PixelRect geometry, WindowState state)
	{
		if (geometry.Width <= 0 || geometry.Height <= 0)
			return;
		// Full-screen is not restored as such - a window that comes back with no chrome and no
		// way to tell why is a window that looks broken - but it is the same intent as
		// maximized, so it comes back that way.
		string kind = state == WindowState.Normal ? "normal" : "maximized";
		UserData.Write(FileName,
			$"{geometry.X} {geometry.Y} {geometry.Width} {geometry.Height} {kind}");
	}

	/// <summary>Applies the position and size that were saved, as far as they still make sense,
	/// and answers with the geometry to keep tracking from and whether the window was left
	/// maximized.</summary>
	static (PixelRect Geometry, bool Maximized) Restore(Window window)
	{
		if (Parse(UserData.Read(FileName)) is not var (geometry, maximized)
			|| !FitsAScreen(window, geometry))
		{
			return (default, false);
		}
		window.WindowStartupLocation = WindowStartupLocation.Manual;
		window.Position = geometry.Position;
		var size = geometry.Size.ToSize(window.DesktopScaling);
		window.Width = Math.Max(MinimumSize, size.Width);
		window.Height = Math.Max(MinimumSize, size.Height);
		return (geometry, maximized);
	}

	/// <summary>Whether a saved rectangle still lands on a screen this machine has, with enough
	/// of its corner reachable to move it.</summary>
	static bool FitsAScreen(Window window, PixelRect geometry)
	{
		var screens = window.Screens;
		if (screens is null || screens.All.Count == 0)
			return false;
		var corner = new PixelRect(geometry.X, geometry.Y,
			Math.Min(VisibleCorner, geometry.Width), Math.Min(VisibleCorner, geometry.Height));
		foreach (var screen in screens.All)
		{
			if (screen.WorkingArea.Intersects(corner))
				return true;
		}
		return false;
	}

	static (PixelRect Geometry, bool Maximized)? Parse(string? line)
	{
		if (line is null)
			return null;
		var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length != 5
			|| !int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)
			|| !int.TryParse(parts[2], out int width) || !int.TryParse(parts[3], out int height)
			|| width <= 0 || height <= 0)
		{
			return null;
		}
		return (new PixelRect(x, y, width, height), parts[4] == "maximized");
	}
}
