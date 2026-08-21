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
		if (Installed() is { } ours)
		{
			CliLog.Write("basedpyright", $"server: {ours.Executable} (installed by this tool)");
			return ours;
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
		CliLog.Write("pyright", "no Python language server found yet; one can be installed "
			+ "into this tool's own cache");
		return null;
	}

	/// <summary>Where a server this tool installed for itself lives: a virtual environment of
	/// its own, so nothing is added to the reader's Python or to their PATH, and deleting the
	/// directory undoes all of it.</summary>
	static string OwnServerRoot => CachePath.For("python-lsp");

	/// <summary>That server, if the install is there and finished.</summary>
	static LspServerSpec? Installed()
	{
		string executable = Path.Combine(OwnServerRoot, OperatingSystem.IsWindows() ? "Scripts" : "bin",
			OperatingSystem.IsWindows() ? "basedpyright-langserver.exe" : "basedpyright-langserver");
		return File.Exists(executable) ? new LspServerSpec("basedpyright", executable, ["--stdio"]) : null;
	}

	/// <summary>
	/// Installs a Python language server, so that reading Python does not first require the
	/// reader to have installed anything themselves.
	///
	/// basedpyright rather than pyright because it is published as a wheel that carries its
	/// own node, so a Python interpreter is the only thing this needs to find - a machine with
	/// Python but no node gets a working server, which is the case npx cannot serve. It is
	/// pyright underneath, so the review reads the same as it does through any of the others.
	///
	/// Everything it does is a command in the log, and it lands in this tool's cache and
	/// nowhere else, because installing software on someone's machine is exactly the kind of
	/// thing they must be able to see afterwards and undo.
	/// </summary>
	public static async Task<LspServerSpec?> InstallPythonAsync(string repoPath, CancellationToken ct)
	{
		if (Installed() is { } already)
			return already;
		if (PythonEnvironment.InterpreterFor(repoPath) is not { } python)
		{
			CliLog.Write("basedpyright", "no Python interpreter to build a server environment with, "
				+ "so none was installed");
			return null;
		}
		try
		{
			CliLog.Write("basedpyright", $"installing a Python language server into {OwnServerRoot} - "
				+ "once per machine, a few hundred MB, and it brings its own node");
			await ExternalTool.RunAsync(python, ["-m", "venv", OwnServerRoot], repoPath, ct);
			string venvPython = Path.Combine(OwnServerRoot, OperatingSystem.IsWindows() ? "Scripts" : "bin",
				OperatingSystem.IsWindows() ? "python.exe" : "python");
			await ExternalTool.RunAsync(venvPython,
				["-m", "pip", "install", "--disable-pip-version-check", "basedpyright"], repoPath, ct);
		}
		catch (ToolFailedException ex)
		{
			CliLog.Write("basedpyright", $"install failed: {ex.Message}");
			return null;
		}
		var installed = Installed();
		CliLog.Write("basedpyright", installed is null
			? $"install finished but there is no server in {OwnServerRoot}"
			: $"installed: {installed.Executable}");
		return installed;
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
	public static string? OnPath(string executable)
	{
		foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
		{
			foreach (string candidate in ExecutableNames(Path.Combine(directory, executable),
				OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("PATHEXT")))
			{
				if (File.Exists(candidate))
					return candidate;
			}
		}
		return null;
	}

	/// <summary>
	/// What a bare command name can actually be started as. Everywhere but Windows that is the
	/// name itself; on Windows it is the name plus one of PATHEXT's extensions, and only those.
	///
	/// npm installs a command twice into the same directory - a POSIX shell script under the
	/// bare name, and the <c>.cmd</c> that Windows runs - so a search that stops at the first
	/// existing file finds <c>npx</c>, hands it to CreateProcess and gets "The specified
	/// executable is not a valid application for this OS platform".
	///
	/// The platform and the extension list are arguments rather than read here, so the machine
	/// this matters on does not have to be the machine that runs the test.
	/// </summary>
	public static IEnumerable<string> ExecutableNames(string path, bool windows, string? pathExt)
	{
		if (!windows || Path.GetExtension(path).Length > 0)
		{
			yield return path;
			yield break;
		}
		foreach (string extension in (pathExt ?? ".COM;.EXE;.BAT;.CMD")
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			yield return path + extension;
	}
}
