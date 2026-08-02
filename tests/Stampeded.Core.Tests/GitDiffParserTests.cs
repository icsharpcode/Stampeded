using NUnit.Framework;

using Stampeded.Core.Diff;
using Stampeded.Core.Git;

namespace Stampeded.Core.Tests;

[TestFixture]
public class GitDiffParserTests
{
	const string SampleDiff = """
		diff --git a/src/Calculator.cs b/src/Calculator.cs
		index 3b18e51..b6fc4c6 100644
		--- a/src/Calculator.cs
		+++ b/src/Calculator.cs
		@@ -1,5 +1,6 @@
		 using System;

		-class Calculator
		+// entry point
		+static class Calculator
		 {
		 	static int Add(int a, int b) => a + b;
		@@ -10,3 +11,4 @@ class Calculator
		 	static int Mul(int a, int b) => a * b;
		 }

		+// trailing note
		diff --git a/README.md b/docs/README.md
		similarity index 95%
		rename from README.md
		rename to docs/README.md
		index 1234567..89abcde 100644
		--- a/README.md
		+++ b/docs/README.md
		@@ -1,2 +1,2 @@
		 # Title
		-old line
		+new line
		diff --git a/removed.txt b/removed.txt
		deleted file mode 100644
		index e69de29..0000000
		--- a/removed.txt
		+++ /dev/null
		@@ -1,1 +0,0 @@
		-goodbye
		diff --git a/added.bin b/added.bin
		new file mode 100644
		index 0000000..f2e41136
		Binary files /dev/null and b/added.bin differ
		diff --git a/new.txt b/new.txt
		new file mode 100644
		index 0000000..3b18e51
		--- /dev/null
		+++ b/new.txt
		@@ -0,0 +1,2 @@
		+hello
		+world
		""";

	[Test]
	public void ParsesModifiedFileWithTwoHunks()
	{
		var files = GitDiffParser.Parse(SampleDiff);

		var calc = files.Single(f => f.NewPath == "src/Calculator.cs");
		Assert.That(calc.Kind, Is.EqualTo(FileChangeKind.Modified));
		Assert.That(calc.IsBinary, Is.False);
		Assert.That(calc.Hunks, Has.Count.EqualTo(2));

		var h1 = calc.Hunks[0];
		Assert.That((h1.OldStart, h1.OldLength, h1.NewStart, h1.NewLength), Is.EqualTo((1, 5, 1, 6)));
		Assert.That(h1.Lines.Select(l => l.Kind), Is.EqualTo(new[] {
			PatchLineKind.Context, PatchLineKind.Context,
			PatchLineKind.Removed,
			PatchLineKind.Added, PatchLineKind.Added,
			PatchLineKind.Context, PatchLineKind.Context,
		}));
		Assert.That(h1.Lines[2].Text, Is.EqualTo("class Calculator"));
		Assert.That(h1.Lines[4].Text, Is.EqualTo("static class Calculator"));

		Assert.That(calc.Hunks[1].Header, Is.EqualTo("class Calculator"));
	}

	[Test]
	public void ParsesRename()
	{
		var files = GitDiffParser.Parse(SampleDiff);

		var readme = files.Single(f => f.NewPath == "docs/README.md");
		Assert.That(readme.Kind, Is.EqualTo(FileChangeKind.Renamed));
		Assert.That(readme.OldPath, Is.EqualTo("README.md"));
		Assert.That(readme.Hunks, Has.Count.EqualTo(1));
	}

	[Test]
	public void ParsesDeletionAndAddition()
	{
		var files = GitDiffParser.Parse(SampleDiff);

		var removed = files.Single(f => f.OldPath == "removed.txt");
		Assert.That(removed.Kind, Is.EqualTo(FileChangeKind.Deleted));
		Assert.That(removed.Path, Is.EqualTo("removed.txt"));
		Assert.That(removed.Hunks[0].Lines.Single().Kind, Is.EqualTo(PatchLineKind.Removed));

		var added = files.Single(f => f.NewPath == "new.txt");
		Assert.That(added.Kind, Is.EqualTo(FileChangeKind.Added));
		Assert.That(added.Hunks[0].Lines.Select(l => l.Text), Is.EqualTo(new[] { "hello", "world" }));
	}

	[Test]
	public void ParsesBinaryFile()
	{
		var files = GitDiffParser.Parse(SampleDiff);

		var bin = files.Single(f => f.NewPath == "added.bin");
		Assert.That(bin.IsBinary, Is.True);
		Assert.That(bin.Kind, Is.EqualTo(FileChangeKind.Added));
		Assert.That(bin.Hunks, Is.Empty);
	}

	[Test]
	public void EmptyDiffYieldsNoFiles()
	{
		Assert.That(GitDiffParser.Parse(""), Is.Empty);
	}
}
