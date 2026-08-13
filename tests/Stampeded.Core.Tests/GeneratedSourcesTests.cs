using NUnit.Framework;

using Stampeded.Core.Diff;
using Stampeded.Core.Testing;

namespace Stampeded.Core.Tests;

/// <summary>
/// Pairing and diffing generator output between the two sides of a review. The build that
/// produces the files is not exercised here - these are the parts that decide what the
/// reviewer ends up seeing.
/// </summary>
public class GeneratedSourcesTests
{
	readonly List<string> temporaryDirectories = [];

	[TearDown]
	public void RemoveTemporaryDirectories()
	{
		foreach (var dir in temporaryDirectories)
		{
			try
			{
				Directory.Delete(dir, recursive: true);
			}
			catch (IOException)
			{
			}
		}
		temporaryDirectories.Clear();
	}

	[Test]
	public void CollectsCompilerOutputAndIgnoresOtherDirectoriesCalledGenerated()
	{
		string tree = NewDirectory();
		Write(tree, "src/Lib/obj/Debug/net10.0/generated/Gen.dll/Gen.Emitter/Thing.g.cs", "// thing");
		// A checked-in directory that happens to have the name, and a build directory that is
		// not the compiler's layout: neither is generator output.
		Write(tree, "src/Lib/generated/HandWritten.cs", "// not from a build");
		Write(tree, "src/Lib/obj/Debug/generated/Odd.cs", "// wrong depth");

		var found = GeneratedSources.Collect(tree);

		Assert.That(found.Keys, Is.EquivalentTo(new[] { "src/Lib/generated/Gen.dll/Gen.Emitter/Thing.g.cs" }));
	}

	[Test]
	public void KeepsOutputOfTheSameGeneratorInDifferentProjectsApart()
	{
		string tree = NewDirectory();
		Write(tree, "src/A/obj/Debug/net10.0/generated/Gen.dll/Gen.Emitter/Thing.g.cs", "// a");
		Write(tree, "src/B/obj/Debug/net10.0/generated/Gen.dll/Gen.Emitter/Thing.g.cs", "// b");

		var found = GeneratedSources.Collect(tree);

		Assert.That(found, Has.Count.EqualTo(2));
		Assert.That(File.ReadAllText(found["src/A/generated/Gen.dll/Gen.Emitter/Thing.g.cs"]), Is.EqualTo("// a"));
		Assert.That(File.ReadAllText(found["src/B/generated/Gen.dll/Gen.Emitter/Thing.g.cs"]), Is.EqualTo("// b"));
	}

	[Test]
	public void PairsTheTwoSidesEvenWhenTheyWereBuiltDifferently()
	{
		string baseTree = NewDirectory(), headTree = NewDirectory();
		Write(baseTree, "src/Lib/obj/Debug/net10.0/generated/G/E/Thing.g.cs", "one\ntwo\n");
		Write(headTree, "src/Lib/obj/Release/net11.0/generated/G/E/Thing.g.cs", "one\ntwo changed\n");

		var files = GeneratedSources.DiffAsync(baseTree, headTree).GetAwaiter().GetResult();

		Assert.That(files, Has.Count.EqualTo(1));
		Assert.That(files[0].Kind, Is.EqualTo(FileChangeKind.Modified));
		Assert.That(files[0].Path, Is.EqualTo("src/Lib/generated/G/E/Thing.g.cs"));
		Assert.That(files[0].Hunks, Is.Not.Empty);
	}

	[Test]
	public void ReportsWhatOnlyOneSideGenerated()
	{
		string baseTree = NewDirectory(), headTree = NewDirectory();
		Write(baseTree, "src/Lib/obj/Debug/net10.0/generated/G/E/Gone.g.cs", "was here\n");
		Write(headTree, "src/Lib/obj/Debug/net10.0/generated/G/E/New.g.cs", "is here\n");

		var files = GeneratedSources.DiffAsync(baseTree, headTree).GetAwaiter().GetResult();

		Assert.That(files.Select(f => (f.Path, f.Kind)), Is.EquivalentTo(new[] {
			("src/Lib/generated/G/E/Gone.g.cs", FileChangeKind.Deleted),
			("src/Lib/generated/G/E/New.g.cs", FileChangeKind.Added),
		}));
	}

	[Test]
	public void LeavesOutGeneratedFilesTheChangeDidNotMove()
	{
		string baseTree = NewDirectory(), headTree = NewDirectory();
		Write(baseTree, "src/Lib/obj/Debug/net10.0/generated/G/E/Same.g.cs", "unchanged\n");
		Write(headTree, "src/Lib/obj/Debug/net10.0/generated/G/E/Same.g.cs", "unchanged\n");

		Assert.That(GeneratedSources.DiffAsync(baseTree, headTree).GetAwaiter().GetResult(), Is.Empty,
			"a generator whose output stands still is not part of the change");
	}

	[Test]
	public void CarriesWhereEachSideCanBeReadFrom()
	{
		string baseTree = NewDirectory(), headTree = NewDirectory();
		Write(baseTree, "src/Lib/obj/Debug/net10.0/generated/G/E/Thing.g.cs", "before\n");
		Write(headTree, "src/Lib/obj/Debug/net10.0/generated/G/E/Thing.g.cs", "after\n");

		var file = GeneratedSources.DiffAsync(baseTree, headTree).GetAwaiter().GetResult().Single();

		// Nothing can read these out of a commit, so the diff has to say where they are.
		Assert.That(file.IsGenerated, Is.True);
		Assert.That(File.ReadAllText(file.Generated!.BaseFile!), Is.EqualTo("before\n"));
		Assert.That(File.ReadAllText(file.Generated!.HeadFile!), Is.EqualTo("after\n"));
	}

	string NewDirectory()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		temporaryDirectories.Add(dir);
		return dir;
	}

	static void Write(string tree, string relativePath, string content)
	{
		string full = Path.Combine(tree, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, content);
	}
}
