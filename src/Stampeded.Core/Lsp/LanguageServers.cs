using Stampeded.Core.Infra;

namespace Stampeded.Core.Lsp;

/// <summary>
/// Which server serves which files, and how to start it.
///
/// Nothing is installed on the reader's behalf and nothing is guessed twice: a server named
/// in the environment wins, then one that is already on PATH, and only then npx - which
/// downloads pyright the first time and says so in the log, because a review that quietly
/// fetches a package from the internet is exactly the kind of thing someone has to be able
/// to explain afterwards.
/// </summary>
public static class LanguageServers
{
	/// <summary>The extensions a language server is worth starting for, per language.</summary>
	public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ExtensionsByLanguage =
		new Dictionary<string, IReadOnlySet<string>> {
			["python"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".py", ".pyi" },
		};

	/// <summary>
	/// The Python server to run, or null when there is none to run. <c>STAMPEDED_PYTHON_LSP</c>
	/// overrides the search with a command line of its own ("<c>pylsp</c>",
	/// "<c>npx basedpyright-langserver --stdio</c>").
	/// </summary>
	public static LspServerSpec? Python()
	{
		if (FromEnvironment("STAMPEDED_PYTHON_LSP") is { } configured)
			return configured;
		foreach (var (executable, arguments) in new (string, string[])[] {
			("pyright-langserver", ["--stdio"]),
			("basedpyright-langserver", ["--stdio"]),
			("jedi-language-server", []),
			("pylsp", []),
		})
		{
			if (OnPath(executable) is { } found)
			{
				CliLog.Write("pyright", $"server: {found} {string.Join(' ', arguments)} (on PATH)");
				return new LspServerSpec(Path.GetFileNameWithoutExtension(found), found, arguments);
			}
		}
		if (OnPath("npx") is { } npx)
		{
			CliLog.Write("pyright", "no Python language server on PATH; running it through npx "
				+ "(the first review downloads pyright into the npm cache)");
			// The executable is pyright-langserver, which lives in the pyright package -
			// npx cannot infer the one from the other, and asking it to guess installs
			// nothing and fails with a 404.
			var throughNpx = new LspServerSpec("pyright", npx,
				["--yes", "--package", "pyright", "--", "pyright-langserver", "--stdio"]);
			CliLog.Write("pyright", $"server: {npx} {string.Join(' ', throughNpx.Arguments)}");
			return throughNpx;
		}
		CliLog.Write("pyright", "no Python language server found: install pyright, "
			+ "or name one in STAMPEDED_PYTHON_LSP");
		return null;
	}

	/// <summary>The Roslyn server built beside the application, for reviewing C# out of
	/// process. Null when it was not deployed next to the executable.</summary>
	public static LspServerSpec? Roslyn()
	{
		if (FromEnvironment("STAMPEDED_CSHARP_LSP") is { } configured)
			return configured;
		CliLog.Write("roslyn-lsp", $"looking for the server beside {AppContext.BaseDirectory}");
		string directory = AppContext.BaseDirectory;
		string executable = Path.Combine(directory,
			OperatingSystem.IsWindows() ? "Stampeded.RoslynLsp.exe" : "Stampeded.RoslynLsp");
		if (File.Exists(executable))
			return new LspServerSpec("roslyn-lsp", executable, []);
		// Running from a source build, where each project has its own output directory.
		string sibling = Path.GetFullPath(Path.Combine(directory, "..", "..", "..", "..",
			"Stampeded.RoslynLsp", "bin", Configuration(directory), "net10.0",
			OperatingSystem.IsWindows() ? "Stampeded.RoslynLsp.exe" : "Stampeded.RoslynLsp"));
		return File.Exists(sibling) ? new LspServerSpec("roslyn-lsp", sibling, []) : null;
	}

	static string Configuration(string directory)
		=> directory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
			? "Release"
			: "Debug";

	static LspServerSpec? FromEnvironment(string variable)
	{
		string? command = Environment.GetEnvironmentVariable(variable);
		if (string.IsNullOrWhiteSpace(command))
			return null;
		var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		string executable = OnPath(parts[0]) ?? parts[0];
		string name = Path.GetFileNameWithoutExtension(parts[0]);
		CliLog.Write(name, $"server: {executable} {string.Join(' ', parts[1..])} (from {variable})"
			+ (File.Exists(executable) ? "" : " - WHICH DOES NOT EXIST"));
		return new LspServerSpec(name, executable, parts[1..]);
	}

	/// <summary>The full path of an executable on PATH, or null. Resolved here rather than
	/// left to the process start so the log can say which one was picked.</summary>
	static string? OnPath(string executable)
	{
		foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			string candidate = Path.Combine(directory, executable);
			if (File.Exists(candidate))
				return candidate;
			if (OperatingSystem.IsWindows() && File.Exists(candidate + ".exe"))
				return candidate + ".exe";
		}
		return null;
	}
}
