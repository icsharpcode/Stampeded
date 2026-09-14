using NUnit.Framework;

using Stampeded.Core.GitHub;

namespace Stampeded.Core.Tests;

/// <summary>
/// A check row opens its failed log by run id, and the only place that id appears is the link
/// `gh pr checks` hands over - which points at the job, not the run. Getting it wrong makes
/// double-clicking a failed check do nothing at all, silently.
/// </summary>
public class CheckRunIdTests
{
	[TestCase("https://github.com/icsharpcode/Stampeded/actions/runs/33841144458/job/100923522142", 33841144458L)]
	[TestCase("https://github.com/icsharpcode/Stampeded/actions/runs/33841144458", 33841144458L)]
	public void ReadsTheRunOutOfAnActionsLink(string link, long runId)
		=> Assert.That(GitHubService.RunIdOf(link), Is.EqualTo(runId));

	// A check reported by anything but Actions links somewhere gh cannot fetch logs from.
	[TestCase("https://dev.azure.com/contoso/Widgets/_build/results?buildId=71")]
	[TestCase("")]
	[TestCase(null)]
	public void AnswersNothingForWhatIsNotAnActionsRun(string? link)
		=> Assert.That(GitHubService.RunIdOf(link), Is.Null);
}
