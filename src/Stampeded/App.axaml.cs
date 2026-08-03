using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Stampeded;

public class App : Application
{
	/// <summary>The one review workspace of this app instance; set by MainViewModel.</summary>
	public static ReviewWorkspace? Workspace { get; set; }

	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new MainWindow();
			if (Program.AutoOpenPr is { } pr)
				Workspace?.OpenPrAsync(pr).HandleExceptions();
		}
		base.OnFrameworkInitializationCompleted();
	}
}
