using Microsoft.CodeAnalysis;

using NUnit.Framework;

using Stampeded.Core.Roslyn;

namespace Stampeded.Core.Tests;

/// <summary>
/// The resolution the impacted-test filter rests on: a changed line has to name the member
/// that contains it, not whatever token happens to sit at some column of that line.
/// </summary>
public class EnclosingMemberTests
{
	[Test]
	public async Task BodyLineResolvesToItsMemberAndFindsTheTestReferencingIt()
	{
		string dir = NewTempDir();
		try
		{
			Directory.CreateDirectory(Path.Combine(dir, "src"));
			Directory.CreateDirectory(Path.Combine(dir, "tests"));
			// Line 5 is inside Infer's body: at column 1 it is indentation, and the first
			// identifier on it is the local, not the method.
			File.WriteAllText(Path.Combine(dir, "src", "TypeInference.cs"), """
				public class TypeInference
				{
					public int Infer(int x)
					{
						int local = x + 1;
						return local;
					}
				}
				""");
			File.WriteAllText(Path.Combine(dir, "tests", "TypeInferenceTests.cs"), """
				public class TypeInferenceTests
				{
					public int Run() => new TypeInference().Infer(1);
				}
				""");

			var service = new RoslynWorkspaceService();
			await service.LoadAsync(dir, null, CancellationToken.None);
			Assert.That(service.State, Is.EqualTo(SemanticState.SyntaxOnly), service.LoadLog);

			var member = await service.GetEnclosingMemberAsync(
				Path.Combine("src", "TypeInference.cs"), line: 5, CancellationToken.None);

			Assert.That(member, Is.Not.Null);
			Assert.That(member!.Name, Is.EqualTo("Infer"), "the member, not the local on that line");

			var hits = await service.FindReferencesAsync(member, CancellationToken.None);
			Assert.That(hits.Select(h => Path.GetFileName(h.FilePath)), Does.Contain("TypeInferenceTests.cs"));

			// Why the enclosing member has to be asked for by line: resolving the same line by
			// column lands on whatever token is there, whose references reach no test at all.
			string rel = Path.Combine("src", "TypeInference.cs");
			int position = (await service.GetPositionAsync(rel, line: 5, column: 20, CancellationToken.None))!.Value;
			var byColumn = await service.GetSymbolAtAsync(rel, position, CancellationToken.None);
			Assert.That(byColumn?.Name, Is.Not.EqualTo("Infer"));
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Test]
	public async Task DeclarationLinesResolveToWhatTheyDeclare()
	{
		string dir = NewTempDir();
		try
		{
			File.WriteAllText(Path.Combine(dir, "C.cs"), """
				public class C : System.IDisposable
				{
					int field;

					public void Dispose() { }
				}
				""");
			var service = new RoslynWorkspaceService();
			await service.LoadAsync(dir, null, CancellationToken.None);

			// The type's own declaration line: outside every member, and not inside the type
			// either as far as the enclosing scope is concerned.
			var onTypeLine = await service.GetEnclosingMemberAsync("C.cs", line: 1, CancellationToken.None);
			// A field declaration is not a scope of its own: its line reports the type, which
			// is also what a test would have to reference to reach the field.
			var onField = await service.GetEnclosingMemberAsync("C.cs", line: 3, CancellationToken.None);
			var onSignature = await service.GetEnclosingMemberAsync("C.cs", line: 5, CancellationToken.None);
			var blank = await service.GetEnclosingMemberAsync("C.cs", line: 4, CancellationToken.None);

			Assert.That(onTypeLine?.Name, Is.EqualTo("C"));
			Assert.That(onField?.Name, Is.EqualTo("C"));
			Assert.That(onSignature?.Name, Is.EqualTo("Dispose"), "a signature names the member it declares");
			Assert.That(blank, Is.Null, "a line with nothing on it names no member");
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		return dir;
	}
}
