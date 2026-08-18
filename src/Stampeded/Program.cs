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
		int prIndex = Array.IndexOf(args, "--pr");
		if (prIndex >= 0 && prIndex + 1 < args.Length && int.TryParse(args[prIndex + 1], out int pr))
			AutoOpenPr = pr;
		// The value of --pr is not the repository, which is what taking the first argument that
		// does not start with a dash made of "--pr 4013 /path/to/repo": the number became the
		// path, and the window opened on a repository that does not exist.
		int prValueIndex = prIndex >= 0 ? prIndex + 1 : -1;
		var repoArg = args
			.Where((a, i) => !a.StartsWith('-') && i != prValueIndex)
			.FirstOrDefault();
		if (repoArg is not null)
			RepoPath = Path.GetFullPath(repoArg);
		BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
	}

	public static AppBuilder BuildAvaloniaApp()
	{
		var builder = AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.With(new X11PlatformOptions { OverlayPopups = true })
			.LogToTrace();
		// Third-party styles (e.g. Markdown.Avalonia's code spans) name Windows
		// fonts outright; an unresolvable family name aborts the layout pass, so
		// map the usual suspects to the fontconfig monospace alias. Only where
		// fontconfig exists: on Windows the named fonts are real and the aliases
		// are the unresolvable ones - with them in place even the default
		// typeface fails to produce a glyph typeface and no window ever shows.
		if (!OperatingSystem.IsWindows())
			builder = builder.With(new Avalonia.Media.FontManagerOptions {
				FontFamilyMappings = new Dictionary<string, Avalonia.Media.FontFamily> {
					["Consolas"] = new("monospace"),
					["Menlo"] = new("monospace"),
					["Monaco"] = new("monospace"),
					["Courier New"] = new("monospace"),
					["Cascadia Code"] = new("monospace"),
					["Segoe UI"] = new("sans-serif"),
				},
			});
		return builder;
	}
}
