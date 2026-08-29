using Avalonia.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Panes;

/// <summary>
/// The dropdown beside the since-last-pass button: which earlier point the scope starts from.
/// The same three the menu bar offers, next to the control that reads them - the answer
/// depends on how the reader works, and a setting they have to go looking for is one they will
/// not know is theirs to make.
/// </summary>
static class PassBaselineFlyout
{
	public static void ShowFor(object? sender)
	{
		if (sender is not Control anchor || App.Workspace is not { } workspace)
			return;
		var menu = new MenuFlyout();
		foreach (var option in workspace.Scopes.PassBaselineOptions)
		{
			var kind = option.Kind;
			var item = new MenuItem {
				// The mark says which point the scope will really start from, so a choice
				// that is not on offer here does not look like the one in force.
				Header = (option.InUse ? "•  " : "     ") + option.Header,
				IsEnabled = option.Available,
			};
			ToolTip.SetTip(item, option.Tip);
			ToolTip.SetShowOnDisabled(item, true);
			item.Click += (_, _) => {
				workspace.Scopes.UsePassBaseline(kind);
				workspace.Scopes.EnterSinceLastPassAsync().HandleExceptions();
			};
			menu.Items.Add(item);
		}
		menu.ShowAt(anchor);
	}
}
