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
			if (PythonVenv.Create(Path.Combine(repo, ".venv")) is not { } interpreter)
			{
				Assert.Ignore("no Python interpreter available");
				return;
			}

			Assert.That(PythonEnvironment.InterpreterFor(repo), Is.EqualTo(interpreter));
		}
		finally
		{
			TempDirectory.Delete(repo);
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
			TempDirectory.Delete(repo);
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
			if (PythonVenv.Create(Path.Combine(repo, ".venv")) is not { } interpreter)
			{
				Assert.Ignore("no Python interpreter available");
				return;
			}
			string package = Path.Combine(PythonVenv.SitePackages(interpreter), "mylib");
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
			TempDirectory.Delete(repo);
			TempDirectory.Delete(worktree);
		}
	}

	static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-pyenv-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}
}
