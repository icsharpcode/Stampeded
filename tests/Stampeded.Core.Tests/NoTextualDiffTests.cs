using NUnit.Framework;

using Stampeded.Core.Diff;

namespace Stampeded.Core.Tests;

/// <summary>
/// What a file with nothing to read says for itself. Both kinds used to open as an empty
/// document, which a reader cannot tell from a tool that failed.
/// </summary>
public class NoTextualDiffTests
{
	static FileDiff Binary(FileChangeKind kind = FileChangeKind.Modified, string path = "docs/logo.png")
		=> new(path, path, kind, IsBinary: true, []);

	static FileDiff Textual(FileChangeKind kind, string oldPath, string newPath)
		=> new(oldPath, newPath, kind, IsBinary: false, []);

	[Test]
	public void AppliesToABinaryFileAndToOneWithNoHunks()
	{
		Assert.That(NoTextualDiff.Applies(Binary()), Is.True);
		Assert.That(NoTextualDiff.Applies(Textual(FileChangeKind.Renamed, "a.md", "b.md")), Is.True);
		Assert.That(NoTextualDiff.Applies(
			new("a.cs", "a.cs", FileChangeKind.Modified, false, [new DiffHunk(1, 1, 1, 1, "", [])])),
			Is.False, "a file with hunks has something to read");
	}

	[Test]
	public void SaysWhichKindOfNothingItIs()
	{
		Assert.That(NoTextualDiff.Badge(Binary()), Is.EqualTo("binary"));
		Assert.That(NoTextualDiff.Badge(Textual(FileChangeKind.Renamed, "a.md", "b.md")), Is.EqualTo("no lines"));
		Assert.That(NoTextualDiff.Badge(
			new("a.cs", "a.cs", FileChangeKind.Modified, false, [new DiffHunk(1, 1, 1, 1, "", [])])),
			Is.Empty);
	}

	[Test]
	public void ComparesTheTwoSidesOfABinaryChange()
	{
		string described = NoTextualDiff.Describe(Binary(), baseSize: 12 * 1024, headSize: 14 * 1024);

		Assert.That(described, Does.Contain("Binary file"));
		Assert.That(described, Does.Contain("Modified."));
		Assert.That(described, Does.Contain("12 KB -> 14 KB (+2 KB)"),
			"the direction and the difference, not two numbers to subtract");
	}

	[Test]
	public void NamesOnlyTheSideThatHasTheFile()
	{
		Assert.That(NoTextualDiff.Describe(Binary(FileChangeKind.Added), null, 2048),
			Does.Contain("Added.  2 KB"));
		Assert.That(NoTextualDiff.Describe(Binary(FileChangeKind.Deleted), 2048, null),
			Does.Contain("Deleted.  was 2 KB"));
	}

	[Test]
	public void SaysWhereARenameCameFrom()
	{
		string described = NoTextualDiff.Describe(
			Textual(FileChangeKind.Renamed, "docs/old-guide.md", "docs/guide.md"), null, null);

		Assert.That(described, Does.Contain("No lines changed"));
		Assert.That(described, Does.Contain("Renamed from docs/old-guide.md."));
	}

	[Test]
	public void ASizeThatDidNotMoveSaysSo()
	{
		// A binary file whose bytes differ at the same length - a rebuilt asset, a re-encoded
		// image - would otherwise read as "4 KB -> 4 KB" and look like a mistake.
		Assert.That(NoTextualDiff.Describe(Binary(), 4096, 4096), Does.Contain("4 KB, unchanged in size"));
	}

	[TestCase(0, "0 bytes")]
	[TestCase(512, "512 bytes")]
	[TestCase(1024, "1 KB")]
	[TestCase(1536, "1.5 KB")]
	[TestCase(1024 * 1024, "1 MB")]
	public void ReadsASizeTheWayAPersonWould(long bytes, string expected)
		=> Assert.That(NoTextualDiff.Size(bytes), Is.EqualTo(expected));
}
