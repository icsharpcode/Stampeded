using NUnit.Framework;

using Stampeded.Core.Roslyn;

namespace Stampeded.Core.Tests;

/// <summary>
/// What a symbol has to survive now that it is a position rather than an object: handed back
/// by one call and understood by the next, in a later request, with nothing of the compiler's
/// own kept in between.
/// </summary>
public class SemanticProviderTests
{
	[Test]
	public async Task AUseResolvesToItsDeclarationThroughTheInterface()
	{
		string dir = NewTempDir();
		try
		{
			File.WriteAllText(Path.Combine(dir, "Greeter.cs"), """
				public class Greeter
				{
					public string Greet() => "hi";

					public string Twice() => Greet() + Greet();
				}
				""");
			ISemanticProvider provider = new RoslynWorkspaceService();
			await ((RoslynWorkspaceService)provider).LoadAsync(dir, null, CancellationToken.None);
			Assert.That(provider.State, Is.EqualTo(SemanticState.SyntaxOnly), provider.LoadLog);

			// The first Greet() of line 5, which is a use and not the declaration.
			int position = (await provider.GetPositionAsync("Greeter.cs", 5, 27, CancellationToken.None))!.Value;
			var symbol = await provider.GetSymbolAtAsync("Greeter.cs", position, CancellationToken.None);

			Assert.That(symbol, Is.Not.Null);
			Assert.That(symbol!.Name, Is.EqualTo("Greet"));
			Assert.That(symbol.ContainingType?.Name, Is.EqualTo("Greeter"));

			var definition = await provider.GetDefinitionAsync(symbol, CancellationToken.None);

			Assert.That(definition, Is.Not.Null, "the use has to lead back to the declaration");
			Assert.That(definition!.Line, Is.EqualTo(3));
			Assert.That(Path.GetFileName(definition.FilePath), Is.EqualTo("Greeter.cs"));

			// Both uses and the declaration; a reference that was found through a
			// re-resolved position is worth nothing if it only ever finds itself.
			var hits = await provider.FindReferencesAsync(symbol, CancellationToken.None);
			Assert.That(hits.Count(h => h.Line == 5), Is.EqualTo(2), "both calls on line 5");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-semantics-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}
}
