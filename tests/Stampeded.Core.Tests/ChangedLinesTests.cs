using NUnit.Framework;

using Stampeded.Core.Diff;
using Stampeded.Core.Git;

namespace Stampeded.Core.Tests;

/// <summary>
/// The line numbers a diff implies. Everything that asks "is this line part of the change"
/// and everything that decides whether a comment can be posted at all reads them from here,
/// so an off-by-one is a comment posted against the wrong code.
/// </summary>
[TestFixture]
public class ChangedLinesTests
{
	// A hunk starting away from line 1, so a bug that ignores the header's numbering shows.
	const string Diff = """
		diff --git a/src/C.cs b/src/C.cs
		index 3b18e51..b6fc4c6 100644
		--- a/src/C.cs
		+++ b/src/C.cs
		@@ -10,3 +10,3 @@ class C
		 	int Keep;
		-	int Gone;
		+	int Added;
		 }
		diff --git a/old.txt b/new.txt
		similarity index 90%
		rename from old.txt
		rename to new.txt
		index 1234567..89abcde 100644
		--- a/old.txt
		+++ b/new.txt
		@@ -1,2 +1,2 @@
		 # Title
		-was
		+is

		""";

	static ChangedLines Parse() => ChangedLines.From(GitDiffParser.Parse(Diff));

	[Test]
	public void CountsAddedAndRemovedFromTheHunkHeadersNumbering()
	{
		var changed = Parse();

		Assert.That(changed.Added("src/C.cs"), Is.EquivalentTo(new[] { 11 }));
		Assert.That(changed.Removed("src/C.cs"), Is.EquivalentTo(new[] { 11 }));
		Assert.That(changed.IsAdded("src/C.cs", 11), Is.True);
		Assert.That(changed.IsAdded("src/C.cs", 10), Is.False, "context is not an addition");
	}

	[Test]
	public void CommentableLinesAreEveryLineTheHunkPrintsOnThatSide()
	{
		var changed = Parse();

		// Context counts: GitHub takes a comment on any line of the diff, not only changed ones.
		Assert.That(changed.CommentableNew("src/C.cs"), Is.EquivalentTo(new[] { 10, 11, 12 }));
		Assert.That(changed.CommentableOld("src/C.cs"), Is.EquivalentTo(new[] { 10, 11, 12 }));
	}

	[Test]
	public void ARenameIsKeyedByThePathEachSideKnowsItUnder()
	{
		var changed = Parse();

		Assert.That(changed.Added("new.txt"), Is.EquivalentTo(new[] { 2 }));
		Assert.That(changed.Removed("old.txt"), Is.EquivalentTo(new[] { 2 }));
		Assert.That(changed.Added("old.txt"), Is.Empty, "the old path names no new-side line");
	}

	[Test]
	public void AFileTheChangeDoesNotTouchAnswersEmptyRatherThanThrowing()
	{
		var changed = Parse();

		Assert.That(changed.Added("unrelated.cs"), Is.Empty);
		Assert.That(changed.CommentableNew("unrelated.cs"), Is.Empty);
		Assert.That(ChangedLines.Empty.Added("src/C.cs"), Is.Empty);
	}

	[Test]
	public void ReportsTheAddedLinesOfEveryFileForWholeChangeTotals()
	{
		var byFile = Parse().AddedByFile.ToDictionary(entry => entry.Path, entry => entry.Lines.Count);

		Assert.That(byFile["src/C.cs"], Is.EqualTo(1));
		Assert.That(byFile["new.txt"], Is.EqualTo(1));
	}
}
