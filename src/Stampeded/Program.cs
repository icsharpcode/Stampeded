using Avalonia;

namespace Stampeded;

internal static class Program
{
	/// <summary>The repository under review: first non-option argument, else the CWD.</summary>
	public static string RepoPath { get; private set; } = Environment.CurrentDirectory;

	[STAThread]
	public static void Main(string[] args)
	{
		// Must run before any Roslyn workspace assembly loads, so MSBuild resolves from
		// the installed SDK.
		Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
		var repoArg = args.FirstOrDefault(a => !a.StartsWith('-'));
		if (repoArg is not null)
			RepoPath = Path.GetFullPath(repoArg);
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp()
	{
		return AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.With(new X11PlatformOptions { OverlayPopups = true })
			.LogToTrace();
	}
}
