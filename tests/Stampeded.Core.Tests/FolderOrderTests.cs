using NUnit.Framework;

using Stampeded.Core.Review;

namespace Stampeded.Core.Tests;

/// <summary>
/// The order the changed-file list is walked in has to be an order that list can show, and it
/// shows a tree.
/// </summary>
public class FolderOrderTests
{
	static string[] Group(params string[] paths) => [.. FolderOrder.ByFolder(paths, p => p)];

	[Test]
	public void ADirectoryLeftAndReturnedToIsOneRun()
	{
		// What "tests first" or "touched since the last pass first" leaves behind: src is
		// entered, left for tests, and entered again.
		Assert.That(
			Group("src/a.cs", "tests/t.cs", "src/b.cs"),
			Is.EqualTo(new[] { "src/a.cs", "src/b.cs", "tests/t.cs" }));
	}

	[Test]
	public void ADirectoryKeepsThePlaceOfItsFirstFile()
	{
		Assert.That(
			Group("tests/t.cs", "src/a.cs"),
			Is.EqualTo(new[] { "tests/t.cs", "src/a.cs" }));
	}

	[Test]
	public void GroupingIsPerLevel()
	{
		// a/b and a/d are both under a, so a is one run - but within it the two stay apart,
		// which is what the tree draws.
		Assert.That(
			Group("a/b/1.cs", "c/1.cs", "a/d/1.cs"),
			Is.EqualTo(new[] { "a/b/1.cs", "a/d/1.cs", "c/1.cs" }));
	}

	[Test]
	public void FilesBesideDirectoriesKeepTheirOrder()
	{
		Assert.That(
			Group("readme.md", "src/a.cs", "license.txt"),
			Is.EqualTo(new[] { "readme.md", "src/a.cs", "license.txt" }));
	}
}
