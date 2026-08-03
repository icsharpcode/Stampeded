using Avalonia;

namespace Stampeded;

internal static class Program
{
	/// <summary>The repository under review: first non-option argument, else the CWD;
	/// changed at runtime by "Open Repository".</summary>
	public static string RepoPath { get; set; } = Environment.CurrentDirectory;

	/// <summary>PR to open right after startup (--pr N), for scripted/diagnostic runs.</summary>
	public static int? AutoOpenPr { get; private set; }

	[STAThread]
	public static void Main(string[] args)
	{
		// Process-wide so every child process inherits it - git, gh, dotnet restore/test
		// AND the MSBuild build hosts MSBuildWorkspace spawns, which no per-invocation
		// environment override reaches. Needed for legacy SHA-1 signatures with the
		// local OpenSSL setup.
		Environment.SetEnvironmentVariable("OPENSSL_ENABLE_SHA1_SIGNATURES", "1");
		// Must run before any Roslyn workspace assembly loads, so MSBuild resolves from
		// the installed SDK.
		Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
		var repoArg = args.FirstOrDefault(a => !a.StartsWith('-'));
		if (repoArg is not null)
			RepoPath = Path.GetFullPath(repoArg);
		int prIndex = Array.IndexOf(args, "--pr");
		if (prIndex >= 0 && prIndex + 1 < args.Length && int.TryParse(args[prIndex + 1], out int pr))
			AutoOpenPr = pr;
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
