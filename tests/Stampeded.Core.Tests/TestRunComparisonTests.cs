using NUnit.Framework;

using Stampeded.Core.Testing;

namespace Stampeded.Core.Tests;

public class TestRunComparisonTests
{
	static TestResult Result(string name, TestOutcome outcome)
		=> new(name, outcome, TimeSpan.Zero, null, null);

	[Test]
	public void ClassifiesNewFixedAndStillFailing()
	{
		var baseRun = new[] {
			Result("A", TestOutcome.Passed),
			Result("B", TestOutcome.Failed),
			Result("C", TestOutcome.Failed),
		};
		var headRun = new[] {
			Result("A", TestOutcome.Failed),   // regressed
			Result("B", TestOutcome.Passed),   // fixed
			Result("C", TestOutcome.Failed),   // still broken
			Result("D", TestOutcome.Failed),   // new test, failing
		};

		var comparison = TestRunComparison.Compare(baseRun, headRun);

		Assert.That(comparison.NewlyFailing.Select(r => r.TestName), Is.EquivalentTo(new[] { "A", "D" }));
		Assert.That(comparison.Fixed.Select(r => r.TestName), Is.EquivalentTo(new[] { "B" }));
		Assert.That(comparison.StillFailing.Select(r => r.TestName), Is.EquivalentTo(new[] { "C" }));
		Assert.That(comparison.BaseFailed, Is.EqualTo(2));
		Assert.That(comparison.HeadFailed, Is.EqualTo(3));
	}

	[Test]
	public void MultiTfmDuplicatesCollapseToOneVerdictPerName()
	{
		var baseRun = new[] {
			Result("A", TestOutcome.Passed),
			Result("A", TestOutcome.Passed),
		};
		var headRun = new[] {
			// One TFM fails, the other passes: the name counts as failing, once.
			Result("A", TestOutcome.Failed),
			Result("A", TestOutcome.Passed),
		};

		var comparison = TestRunComparison.Compare(baseRun, headRun);

		Assert.That(comparison.NewlyFailing.Select(r => r.TestName), Is.EquivalentTo(new[] { "A" }));
		Assert.That(comparison.Fixed, Is.Empty);
		Assert.That(comparison.HeadFailed, Is.EqualTo(1));
	}

	[Test]
	public void PassingEverywhereYieldsEmptyBuckets()
	{
		var run = new[] { Result("A", TestOutcome.Passed), Result("B", TestOutcome.Skipped) };

		var comparison = TestRunComparison.Compare(run, run);

		Assert.That(comparison.NewlyFailing, Is.Empty);
		Assert.That(comparison.Fixed, Is.Empty);
		Assert.That(comparison.StillFailing, Is.Empty);
	}
}
