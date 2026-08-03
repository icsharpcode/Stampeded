using NUnit.Framework;

using Stampeded.Core.Diff;
using Stampeded.Core.Review;

namespace Stampeded.Core.Tests;

public class TriageEstimateTests
{
	static FileDiff File(string path, int added, int removed)
	{
		var lines = Enumerable.Repeat(new PatchLine(PatchLineKind.Added, "x"), added)
			.Concat(Enumerable.Repeat(new PatchLine(PatchLineKind.Removed, "y"), removed))
			.ToList();
		return new FileDiff(path, path, FileChangeKind.Modified, false,
			[new DiffHunk(1, removed, 1, added, "@@", lines)]);
	}

	[Test]
	public void TestHeavyChangesArePricedAtScanningSpeed()
	{
		// Mirrors PR 3933: 803 test-fixture lines and a 10-line transform change should
		// not be priced like 813 lines of implementation.
		var totals = TriageEstimate.Compute([
			File("ICSharpCode.Decompiler.Tests/TestCases/Pretty/CompoundAssignmentTest.cs", 803, 0),
			File("ICSharpCode.Decompiler/CSharp/Transforms/PrettifyAssignments.cs", 7, 3),
		]);

		Assert.That(totals.TestChanged, Is.EqualTo(803));
		Assert.That(totals.ImplChanged, Is.EqualTo(10));
		// 803/15 -> 54, 10/5 -> 2.
		Assert.That(totals.Minutes, Is.EqualTo(56));
		Assert.That(totals.Sittings, Is.EqualTo(1));
		Assert.That(totals.Rows[0].Path, Does.Contain("CompoundAssignmentTest"));
	}

	[TestCase("src/App/packages.lock.json", FileCategory.Dependency)]
	[TestCase("Directory.Packages.props", FileCategory.Dependency)]
	[TestCase("src/Views/MainWindow.g.cs", FileCategory.Generated)]
	[TestCase("Forms/Grid.Designer.cs", FileCategory.Generated)]
	[TestCase("tests/Core.Tests/FooTests.cs", FileCategory.Test)]
	[TestCase("src/Core/Engine.cs", FileCategory.Implementation)]
	public void CategorizesByPath(string path, FileCategory expected)
	{
		Assert.That(TriageEstimate.Categorize(path), Is.EqualTo(expected));
	}

	[Test]
	public void DependencyFilesArePricedFlat()
	{
		var totals = TriageEstimate.Compute([File("packages.lock.json", 5000, 4000)]);
		Assert.That(totals.Minutes, Is.EqualTo(2));
		Assert.That(totals.DependencyFiles, Is.EqualTo(1));
	}
}
