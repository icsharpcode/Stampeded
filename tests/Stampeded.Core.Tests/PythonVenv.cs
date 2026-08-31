using Stampeded.Core.Lsp;

namespace Stampeded.Core.Tests;

/// <summary>
/// A real virtual environment for a test to point a language server at.
///
/// Real because a hand-built one is not an interpreter: a pyvenv.cfg beside a symlink is how a
/// venv looks, but whether Python recognises it depends on what the symlink resolves to. On
/// macOS /usr/bin/python3 is the Command Line Tools stub, which re-execs the real binary, so
/// sys.executable is the framework's path, the planted pyvenv.cfg is never seen and the
/// environment's site-packages is never on sys.path. `python -m venv` gets this right on every
/// platform, takes a fraction of a second without pip, and lays the directories out the way the
/// platform actually does.
/// </summary>
static class PythonVenv
{
	/// <summary>The interpreter inside a new environment at <paramref name="root"/>, or null on
	/// a machine with no Python - where a test that needs one has nothing to say.</summary>
	public static string? Create(string root)
	{
		if ((LanguageServers.OnPath("python3") ?? LanguageServers.OnPath("python")) is not { } python)
			return null;
		// No pip: it is a download and a second or two, and nothing here installs a package.
		if (Run(python, "-m", "venv", "--without-pip", root) is null)
			return null;
		string interpreter = Path.Combine(root, OperatingSystem.IsWindows() ? "Scripts" : "bin",
			OperatingSystem.IsWindows() ? "python.exe" : "python");
		return File.Exists(interpreter) ? interpreter : null;
	}

	/// <summary>Where a package has to be written for that interpreter to import it, asked of
	/// the interpreter rather than guessed - the layout differs by platform and by version.</summary>
	public static string SitePackages(string interpreter)
		=> Run(interpreter, "-c", "import sysconfig; print(sysconfig.get_paths()['purelib'])")
			?? throw new InvalidOperationException($"{interpreter} does not report its site-packages");

	/// <summary>The command's standard output, or null if it did not run or failed.</summary>
	static string? Run(string executable, params string[] arguments)
	{
		var start = new System.Diagnostics.ProcessStartInfo(executable) { RedirectStandardOutput = true };
		foreach (string argument in arguments)
			start.ArgumentList.Add(argument);
		using var process = System.Diagnostics.Process.Start(start);
		if (process is null)
			return null;
		string output = process.StandardOutput.ReadToEnd().Trim();
		process.WaitForExit(TimeSpan.FromMinutes(1));
		return process.HasExited && process.ExitCode == 0 ? output : null;
	}
}
