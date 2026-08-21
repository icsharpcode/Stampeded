using NUnit.Framework;

using Stampeded.Core.Lsp;

namespace Stampeded.Core.Tests;

/// <summary>
/// What a project's own pyright configuration does to a review. It is committed, so the
/// worktree has it, and it is read relative to itself - which for a virtual environment means
/// pointing at a directory the worktree does not have. The interpreter this tool supplies has
/// to survive that.
/// </summary>
public class PythonProjectConfigTests
{
	[Test]
	public async Task AProjectConfigNamingItsOwnVenvDoesNotBlindTheReview()
	{
		if (LanguageServers.Python() is not { } spec)
		{
			Assert.Ignore("no Python language server available");
			return;
		}
		string clone = NewTempDir();
		string worktree = NewTempDir();
		LspConnection? connection = null;
		try
		{
			string interpreter = FakeVenv(clone);
			// The worktree is a checkout: it has the committed config, and no environment.
			File.WriteAllText(Path.Combine(worktree, "pyproject.toml"), """
				[tool.pyright]
				venvPath = "."
				venv = ".venv"
				""");
			File.WriteAllText(Path.Combine(worktree, "app.py"), """
				import mylib

				print(mylib.hello("world"))
				""");

			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
			connection = await LspConnection.StartAsync(spec, worktree, timeout.Token,
				PythonEnvironment.InitializationOptions(interpreter),
				section => PythonEnvironment.SettingsFor(section, interpreter));
			using var provider = new LspSemanticProvider(connection, worktree, spec.Name);

			int position = (await provider.GetPositionAsync("app.py", 3, 13, timeout.Token))!.Value;
			var symbol = await provider.GetSymbolAtAsync("app.py", position, timeout.Token);
			var definition = await provider.GetDefinitionAsync(symbol!, timeout.Token);

			Assert.That(definition, Is.Not.Null,
				"a venvPath relative to a checkout that has no venv must not win over the one we supply");
			Assert.That(definition!.FilePath, Does.Contain("mylib"));
		}
		finally
		{
			connection?.Dispose();
			Directory.Delete(clone, recursive: true);
			Directory.Delete(worktree, recursive: true);
		}
	}

	static string FakeVenv(string root)
	{
		string version = PythonVersionOf(SystemPython());
		string bin = OperatingSystem.IsWindows() ? "Scripts" : "bin";
		Directory.CreateDirectory(Path.Combine(root, ".venv", bin));
		string packages = Path.Combine(root, ".venv", "lib", "python" + version, "site-packages", "mylib");
		Directory.CreateDirectory(packages);
		File.WriteAllText(Path.Combine(packages, "__init__.py"), """
			def hello(name):
				return "hi " + name
			""");
		File.WriteAllText(Path.Combine(root, ".venv", "pyvenv.cfg"), $"home = /usr/bin\nversion = {version}\n");
		string interpreter = Path.Combine(root, ".venv", bin, OperatingSystem.IsWindows() ? "python.exe" : "python");
		File.CreateSymbolicLink(interpreter, SystemPython());
		return interpreter;
	}

	static string SystemPython()
		=> new[] { "/usr/bin/python3", "/usr/local/bin/python3" }.FirstOrDefault(File.Exists)
			?? throw new InvalidOperationException("no system python3");

	static string PythonVersionOf(string interpreter)
	{
		var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(interpreter) {
			ArgumentList = { "-c", "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')" },
			RedirectStandardOutput = true,
		})!;
		string version = process.StandardOutput.ReadToEnd().Trim();
		process.WaitForExit();
		return version;
	}

	static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-pyproject-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}
}
