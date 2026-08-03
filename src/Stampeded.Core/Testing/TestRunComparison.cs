namespace Stampeded.Core.Testing;

/// <summary>
/// Verdict-level comparison of two test runs (base vs head of a review): the question a
/// text diff of outputs cannot answer - did THIS change introduce the failure, or was it
/// already broken at base?
/// </summary>
public sealed record TestRunComparison(
	IReadOnlyList<TestResult> NewlyFailing,
	IReadOnlyList<TestResult> Fixed,
	IReadOnlyList<TestResult> StillFailing,
	int BasePassed, int BaseFailed, int HeadPassed, int HeadFailed)
{
	public static TestRunComparison Compare(IReadOnlyList<TestResult> baseResults, IReadOnlyList<TestResult> headResults)
	{
		// A test name can appear once per target framework; one failing result marks
		// the name failing.
		var baseFailed = Names(baseResults, TestOutcome.Failed);
		var basePresent = baseResults.Select(r => r.TestName).ToHashSet(StringComparer.Ordinal);
		var headFailedNames = Names(headResults, TestOutcome.Failed);

		var newlyFailing = new List<TestResult>();
		var stillFailing = new List<TestResult>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var result in headResults.Where(r => r.Outcome == TestOutcome.Failed))
		{
			if (!seen.Add(result.TestName))
				continue;
			// A test absent at base (newly written) that fails is a new failure too.
			if (baseFailed.Contains(result.TestName))
				stillFailing.Add(result);
			else
				newlyFailing.Add(result);
		}

		var fixedTests = new List<TestResult>();
		seen.Clear();
		foreach (var result in headResults.Where(r => r.Outcome == TestOutcome.Passed))
		{
			if (seen.Add(result.TestName) && baseFailed.Contains(result.TestName) && !headFailedNames.Contains(result.TestName))
				fixedTests.Add(result);
		}

		return new TestRunComparison(
			newlyFailing, fixedTests, stillFailing,
			BasePassed: Names(baseResults, TestOutcome.Passed).Count,
			BaseFailed: baseFailed.Count,
			HeadPassed: Names(headResults, TestOutcome.Passed).Count,
			HeadFailed: headFailedNames.Count);
	}

	static HashSet<string> Names(IReadOnlyList<TestResult> results, TestOutcome outcome)
		=> results.Where(r => r.Outcome == outcome).Select(r => r.TestName).ToHashSet(StringComparer.Ordinal);
}
