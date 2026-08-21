using Stampeded.Core.Infra;

namespace Stampeded.Core.Lsp;

/// <summary>
/// Which Python a Python server should resolve imports against.
///
/// The files under review live in a worktree - a detached checkout of one commit - and a
/// virtual environment is not committed, so it is never in there. An interpreter path is just
/// a path, though: it can point into the reader's own checkout while the files being analysed
/// sit somewhere else entirely, which is what makes a review of a project's code see the
/// project's dependencies at all.
///
/// Asked of the repository, not of the worktree, and answered in the order a reader would:
/// what they said explicitly, the environment they are working in, the one their project
/// keeps, and failing all of that whatever <c>python3</c> means on this machine.
/// </summary>
public static class PythonEnvironment
{
	/// <summary>The interpreter to hand the server, with the reason it was chosen; the
	/// reason goes in the log, because "which python" is exactly what someone ends up
	/// having to explain when an import does not resolve.</summary>
	public static string? InterpreterFor(string repoPath)
	{
		var passed = new List<string>();
		foreach (var (reason, candidate) in Candidates(repoPath))
		{
			if (candidate is null)
			{
				passed.Add($"{reason}: not set");
				continue;
			}
			if (!File.Exists(candidate))
			{
				passed.Add($"{reason}: {candidate} does not exist");
				continue;
			}
			CliLog.Write("pyright", $"interpreter: {candidate} ({reason})");
			// The ones that lost, in order: on another machine the question is never "which
			// did it pick" but "why not mine", and that is answered by what it looked at.
			if (passed.Count > 0)
				CliLog.Write("pyright", "interpreter, passed over: " + string.Join("; ", passed));
			return candidate;
		}
		CliLog.Write("pyright", "no interpreter found, so imports resolve against whatever the "
			+ "server finds itself. Looked at: " + string.Join("; ", passed));
		return null;
	}

	static IEnumerable<(string Reason, string? Path)> Candidates(string repoPath)
	{
		yield return ("STAMPEDED_PYTHON_PATH", Environment.GetEnvironmentVariable("STAMPEDED_PYTHON_PATH"));
		// An activated environment is inherited through the environment this process was
		// started with, which is the reader saying which one they mean by working in it.
		if (Environment.GetEnvironmentVariable("VIRTUAL_ENV") is { Length: > 0 } active)
			yield return ("active virtual environment", InEnvironment(active));
		if (Environment.GetEnvironmentVariable("CONDA_PREFIX") is { Length: > 0 } conda)
			yield return ("active conda environment", InEnvironment(conda));
		foreach (string name in new[] { ".venv", "venv", "env" })
			yield return ($"{name} in the repository", InEnvironment(Path.Combine(repoPath, name)));
		foreach (string executable in new[] { "python3", "python" })
			yield return ("PATH", LanguageServers.OnPath(executable));
	}

	/// <summary>The interpreter inside an environment directory, however this platform lays
	/// one out.</summary>
	static string InEnvironment(string root)
		=> OperatingSystem.IsWindows()
			? Path.Combine(root, "Scripts", "python.exe")
			: Path.Combine(root, "bin", "python");

	/// <summary>
	/// The settings a server asks for by section. Servers disagree about where the interpreter
	/// is named - pyright reads <c>python.pythonPath</c>, jedi wants it at initialize - so it
	/// is offered in every place a server we might start looks for it, and a server that does
	/// not recognise a key ignores it.
	/// </summary>
	public static object SettingsFor(string section, string? interpreter) => section switch {
		// Asked for as one section or as two depending on the server and its version, so the
		// analysis settings are nested inside the python answer as well as standing alone.
		"python" => new { pythonPath = interpreter, defaultInterpreterPath = interpreter, analysis = Analysis },
		"python.analysis" or "basedpyright.analysis" => Analysis,
		_ => new { },
	};

	/// <summary>
	/// Trace makes pyright report the interpreter it settled on and every path it searches for
	/// imports, into this tool's own log - which is the whole answer to "why is this import
	/// unresolved on that machine".
	/// </summary>
	static object Analysis => new {
		autoSearchPaths = true,
		useLibraryCodeForTypes = true,
		logLevel = LspConnection.Tracing ? "Trace" : "Information",
	};

	/// <summary>The same answer for a server that takes its configuration at initialize.</summary>
	public static object InitializationOptions(string? interpreter) => new {
		python = new { pythonPath = interpreter, defaultInterpreterPath = interpreter },
		// jedi-language-server's name for it.
		workspace = new { environmentPath = interpreter },
	};
}
