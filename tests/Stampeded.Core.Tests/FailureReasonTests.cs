using NUnit.Framework;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

public class FailureReasonTests
{
	[Test]
	public void TakesTheFirstMeaningfulLineOfStdErr()
	{
		Assert.That(ExternalTool.FailureReason("\nfatal: 'topic' is already checked out at '/tmp/wt'\n", ""),
			Is.EqualTo("fatal: 'topic' is already checked out at '/tmp/wt'"));
	}

	[Test]
	public void FallsBackToStdOutWhenStdErrIsEmpty()
	{
		Assert.That(ExternalTool.FailureReason("   \n\n", "gh: Can not approve your own pull request (HTTP 422)"),
			Is.EqualTo("gh: Can not approve your own pull request (HTTP 422)"));
	}

	[Test]
	public void SaysSoWhenTheToolPrintedNothingAtAll()
	{
		Assert.That(ExternalTool.FailureReason("", ""), Is.EqualTo("no output"));
	}

	[Test]
	public void TruncatesAReasonTooLongForALogLine()
	{
		string reason = ExternalTool.FailureReason(new string('x', 500), "");
		Assert.That(reason, Has.Length.EqualTo(203));
		Assert.That(reason, Does.EndWith("..."));
	}
}
