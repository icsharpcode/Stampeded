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
			if (CloneWithPackage(clone) is not { } interpreter)
			{
				Assert.Ignore("no Python interpreter available");
				return;
			}
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
			TempDirectory.Delete(clone);
			TempDirectory.Delete(worktree);
		}
	}

	/// <summary>The reader's own clone: an environment with the package the project depends
	/// on, which is the thing the worktree does not have.</summary>
	static string? CloneWithPackage(string root)
	{
		if (PythonVenv.Create(Path.Combine(root, ".venv")) is not { } interpreter)
			return null;
		string packages = Path.Combine(PythonVenv.SitePackages(interpreter), "mylib");
		Directory.CreateDirectory(packages);
		File.WriteAllText(Path.Combine(packages, "__init__.py"), """
			def hello(name):
				return "hi " + name
			""");
		return interpreter;
	}

	static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-pyproject-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}
}
