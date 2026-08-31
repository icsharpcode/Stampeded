using NUnit.Framework;

using Stampeded.Core.Lsp;

namespace Stampeded.Core.Tests;

/// <summary>
/// A language the tool has no compiler for, answered by a server it did not write. This is
/// the point of the whole provider interface, so it is worth a test that talks to the real
/// thing - skipped where no Python server is installed, since it cannot be faked without
/// testing the fake instead.
/// </summary>
public class PythonLspTests
{
	[Test]
	public async Task PyrightAnswersDefinitionsAndReferencesAcrossFiles()
	{
		if (LanguageServers.Python() is not { } spec)
		{
			Assert.Ignore("no Python language server available");
			return;
		}
		string dir = NewTempDir();
		LspConnection? connection = null;
		try
		{
			File.WriteAllText(Path.Combine(dir, "greeting.py"), """
				def greet(name):
					return "hi " + name


				def shout(name):
					return greet(name).upper()
				""");
			File.WriteAllText(Path.Combine(dir, "main.py"), """
				from greeting import greet

				print(greet("world"))
				""");
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
			connection = await LspConnection.StartAsync(spec, dir, timeout.Token);
			using var provider = new LspSemanticProvider(connection, dir, spec.Name);

			// The call inside shout(), which is defined at the top of the same file.
			int position = (await provider.GetPositionAsync("greeting.py", 6, 9, timeout.Token))!.Value;
			var symbol = await provider.GetSymbolAtAsync("greeting.py", position, timeout.Token);

			Assert.That(symbol?.Name, Is.EqualTo("greet"), "the word under the position is the symbol");

			var definition = await provider.GetDefinitionAsync(symbol!, timeout.Token);
			Assert.That(definition, Is.Not.Null, "pyright has to resolve the call to its def");
			Assert.That(definition!.Line, Is.EqualTo(1));

			// Opening the other file is what puts it in the server's world; a review opens
			// the files it shows, and this is the same thing happening.
			await provider.GetDocumentTextAsync("main.py", timeout.Token);
			var hits = await provider.FindReferencesAsync(symbol!, timeout.Token);
			Assert.That(hits.Select(h => Path.GetFileName(h.FilePath)).Distinct(),
				Does.Contain("main.py"), "the import and the call in the other file");

			var hover = await provider.GetHoverTextAsync("greeting.py", position, timeout.Token);
			Assert.That(hover, Does.Contain("greet"));

			var members = await provider.ListMemberDisplaysAsync("greeting.py", timeout.Token);
			Assert.That(members, Does.Contain("greet"));
			Assert.That(members, Does.Contain("shout"));

			var enclosing = await provider.GetEnclosingMemberAsync("greeting.py", 6, timeout.Token);
			Assert.That(enclosing?.Name, Is.EqualTo("shout"), "the def the changed line is inside");

			// What the Structure pane draws and what the editor folds, for a language with no
			// parser in this process at all.
			string sideText = (await provider.GetDocumentTextAsync("greeting.py", timeout.Token))!;
			var outline = await provider.GetOutlineAsync("greeting.py", sideText, timeout.Token);
			Assert.That(outline.Select(n => n.Title), Is.EquivalentTo(new[] { "greet", "shout" }));
			Assert.That(outline[0].StartLine, Is.EqualTo(1));
			Assert.That(outline[0].Children, Is.Empty, "a def's parameters and locals are not places to jump to");

			var folds = await provider.GetFoldRegionsAsync("greeting.py", sideText, timeout.Token);
			Assert.That(folds.Count, Is.EqualTo(2), "one per def");
			Assert.That(folds[0].StartLine, Is.EqualTo(1));
			Assert.That(folds[0].EndLine, Is.EqualTo(2));

			// A revision the server was not told about is not something it can be asked about:
			// an outline drawn at another text's line numbers is worse than none.
			Assert.That(await provider.GetOutlineAsync("greeting.py", "def other():\n\tpass", timeout.Token),
				Is.Empty);
		}
		finally
		{
			connection?.Dispose();
			TempDirectory.Delete(dir);
		}
	}

	static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-python-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}
}
