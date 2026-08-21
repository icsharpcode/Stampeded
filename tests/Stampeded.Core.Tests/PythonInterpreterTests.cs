using NUnit.Framework;

using Stampeded.Core.Lsp;

namespace Stampeded.Core.Tests;

/// <summary>
/// The arrangement every Python review is: the files are in a worktree, which is a checkout
/// of one commit and therefore has no virtual environment in it, while the environment the
/// project actually uses sits in the reader's own clone. Unless the server is told about that
/// interpreter, an import of anything the project depends on resolves to nothing.
/// </summary>
public class PythonInterpreterTests
{
	[Test]
	public void TheProjectsOwnEnvironmentIsPreferredToWhateverIsOnPath()
	{
		string repo = NewTempDir();
		try
		{
			string interpreter = FakeEnvironment(Path.Combine(repo, ".venv"));

			Assert.That(PythonEnvironment.InterpreterFor(repo), Is.EqualTo(interpreter));
		}
		finally
		{
			Directory.Delete(repo, recursive: true);
		}
	}

	[Test]
	public void WithoutOneTheAnswerIsWhateverPythonMeansHere()
	{
		string repo = NewTempDir();
		try
		{
			string? found = PythonEnvironment.InterpreterFor(repo);

			// Either a python on PATH or nothing at all; what must not happen is a path
			// inside the repository, which has no environment in it.
			Assert.That(found, Is.Null.Or.Not.StartWith(repo));
		}
		finally
		{
			Directory.Delete(repo, recursive: true);
		}
	}

	[Test]
	public async Task AnImportResolvesIntoAnEnvironmentThatIsNotInTheWorktree()
	{
		if (LanguageServers.Python() is not { } spec)
		{
			Assert.Ignore("no Python language server available");
			return;
		}
		string repo = NewTempDir();
		string worktree = NewTempDir();
		LspConnection? connection = null;
		try
		{
			// The clone: an environment with one package in it, and nothing else.
			string interpreter = FakeEnvironment(Path.Combine(repo, ".venv"));
			string package = Path.Combine(repo, ".venv", "lib", "python" + PythonVersion(), "site-packages", "mylib");
			Directory.CreateDirectory(package);
			File.WriteAllText(Path.Combine(package, "__init__.py"), """
				def hello(name):
					return "hi " + name
				""");

			// The worktree: the file under review, importing it. No environment here.
			File.WriteAllText(Path.Combine(worktree, "app.py"), """
				import mylib

				print(mylib.hello("world"))
				""");

			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
			connection = await LspConnection.StartAsync(spec, worktree, timeout.Token,
				PythonEnvironment.InitializationOptions(interpreter),
				section => PythonEnvironment.SettingsFor(section, interpreter));
			using var provider = new LspSemanticProvider(connection, worktree, spec.Name);

			// hello, on the call in the worktree's file.
			int position = (await provider.GetPositionAsync("app.py", 3, 13, timeout.Token))!.Value;
			var symbol = await provider.GetSymbolAtAsync("app.py", position, timeout.Token);
			Assert.That(symbol?.Name, Is.EqualTo("hello"));

			var definition = await provider.GetDefinitionAsync(symbol!, timeout.Token);

			Assert.That(definition, Is.Not.Null,
				"the package is only reachable through the interpreter the server was handed");
			Assert.That(definition!.FilePath, Does.Contain("mylib"));
			Assert.That(definition.Line, Is.EqualTo(1));
		}
		finally
		{
			connection?.Dispose();
			Directory.Delete(repo, recursive: true);
			Directory.Delete(worktree, recursive: true);
		}
	}

	/// <summary>
	/// A virtual environment as Python itself recognises one: a pyvenv.cfg beside a bin
	/// directory whose python is the system's. Building a real one would take a minute and
	/// prove the same thing, which is that an interpreter reports its own site-packages.
	/// </summary>
	static string FakeEnvironment(string root)
	{
		string bin = OperatingSystem.IsWindows() ? "Scripts" : "bin";
		Directory.CreateDirectory(Path.Combine(root, bin));
		File.WriteAllText(Path.Combine(root, "pyvenv.cfg"),
			$"home = /usr/bin\nversion = {PythonVersion()}\n");
		string interpreter = Path.Combine(root, bin, OperatingSystem.IsWindows() ? "python.exe" : "python");
		File.CreateSymbolicLink(interpreter, SystemPython());
		return interpreter;
	}

	static string SystemPython()
		=> new[] { "/usr/bin/python3", "/usr/local/bin/python3" }.FirstOrDefault(File.Exists)
			?? throw new InvalidOperationException("no system python3");

	/// <summary>The system interpreter's major.minor, which is what names its site-packages
	/// directory.</summary>
	static string PythonVersion()
	{
		var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SystemPython()) {
			ArgumentList = { "-c", "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')" },
			RedirectStandardOutput = true,
		})!;
		string version = process.StandardOutput.ReadToEnd().Trim();
		process.WaitForExit();
		return version;
	}

	static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-pyenv-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}
}
