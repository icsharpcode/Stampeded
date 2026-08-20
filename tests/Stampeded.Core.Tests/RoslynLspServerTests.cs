using NUnit.Framework;

using Stampeded.Core.Lsp;

namespace Stampeded.Core.Tests;

/// <summary>
/// The protocol end to end: the review's questions, asked of a real server process over
/// stdin and stdout, and answered from a workspace in another process entirely. Both halves
/// are exercised at once on purpose - a client that frames its messages the way its own
/// server parses them proves nothing unless one of them is right, and this is the pair that
/// has to interoperate with servers nobody here wrote.
/// </summary>
public class RoslynLspServerTests
{
	[Test]
	public async Task ADefinitionAndItsReferencesComeBackOverTheProtocol()
	{
		if (ServerExecutable() is not { } server)
		{
			Assert.Ignore("Stampeded.RoslynLsp is not built");
			return;
		}
		string dir = NewTempDir();
		LspConnection? connection = null;
		try
		{
			File.WriteAllText(Path.Combine(dir, "Greeter.cs"), """
				public class Greeter
				{
					public string Greet() => "hi";

					public string Twice() => Greet() + Greet();
				}
				""");
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
			connection = await LspConnection.StartAsync(
				new LspServerSpec("roslyn-lsp", server, []), dir, timeout.Token);
			using var provider = new LspSemanticProvider(connection, dir, "roslyn-lsp");

			await WaitForLoadAsync(provider, timeout.Token);
			Assert.That(provider.State, Is.EqualTo(SemanticState.SyntaxOnly).Or.EqualTo(SemanticState.Ready),
				provider.StateDetail);

			// The first Greet() of line 5: a use, whose definition is on line 3.
			int position = (await provider.GetPositionAsync("Greeter.cs", 5, 27, timeout.Token))!.Value;
			var symbol = await provider.GetSymbolAtAsync("Greeter.cs", position, timeout.Token);

			Assert.That(symbol?.Name, Is.EqualTo("Greet"));

			var definition = await provider.GetDefinitionAsync(symbol!, timeout.Token);
			Assert.That(definition, Is.Not.Null, "the server has to answer textDocument/definition");
			Assert.That(definition!.Line, Is.EqualTo(3));

			var hits = await provider.FindReferencesAsync(symbol!, timeout.Token);
			Assert.That(hits.Count(h => h.Line == 5), Is.EqualTo(2), "both calls on line 5");
			Assert.That(hits.First().LineText, Is.Not.Empty, "the client fills in the line a hit sits on");

			// The same displays the change map is keyed on: a member named under its type.
			var members = await provider.ListMemberDisplaysAsync("Greeter.cs", timeout.Token);
			Assert.That(members, Does.Contain("Greeter.Greet()"));

			var enclosing = await provider.GetEnclosingMemberAsync("Greeter.cs", 5, timeout.Token);
			Assert.That(enclosing?.Display, Is.EqualTo("Greeter.Twice()"));
		}
		finally
		{
			connection?.Dispose();
			Directory.Delete(dir, recursive: true);
		}
	}

	static async Task WaitForLoadAsync(LspSemanticProvider provider, CancellationToken ct)
	{
		while (provider.State is SemanticState.NotLoaded or SemanticState.Restoring or SemanticState.Loading)
		{
			ct.ThrowIfCancellationRequested();
			await Task.Delay(100, ct);
		}
	}

	/// <summary>The server as built beside these tests, or null when only the test project
	/// was built - which is a skip rather than a failure.</summary>
	static string? ServerExecutable()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stampeded.slnx")))
			directory = directory.Parent;
		if (directory is null)
			return null;
		string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
			? "Release"
			: "Debug";
		string path = Path.Combine(directory.FullName, "src", "Stampeded.RoslynLsp", "bin", configuration,
			"net10.0", OperatingSystem.IsWindows() ? "Stampeded.RoslynLsp.exe" : "Stampeded.RoslynLsp");
		return File.Exists(path) ? path : null;
	}

	static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-lsp-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}
}
