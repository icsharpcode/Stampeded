using System.Text.Json;

using NUnit.Framework;

using Stampeded.Core.GitHub;

namespace Stampeded.Core.Tests;

/// <summary>
/// What the merge line says. GitHub answers with the kind of refusal - "MERGEABLE / BLOCKED" -
/// which is the same two words whether a check is still running or a review has not been given,
/// so it tells the reader nothing about what to do next. The reason is in the fields alongside.
/// </summary>
public class MergeStateSummaryTests
{
	[Test]
	public void BlockedByRunningChecksSaysSo()
	{
		var state = Blocked(Checks(("build", "IN_PROGRESS"), ("test", "QUEUED")));

		Assert.That(state.Summary, Is.EqualTo("2 checks still running"));
		Assert.That(state.Describe, Is.EqualTo("MERGEABLE / BLOCKED"), "the raw pair is still there for the tooltip");
	}

	[Test]
	public void OneRunningCheckIsNotPluralised()
	{
		Assert.That(Blocked(Checks(("build", "IN_PROGRESS"))).Summary, Is.EqualTo("1 check still running"));
	}

	[Test]
	public void AFailingCheckOutranksARunningOne()
	{
		// What has to be fixed comes before what has to be waited for.
		var state = Blocked(Checks(("build", "FAILURE"), ("test", "IN_PROGRESS")));

		Assert.That(state.Summary, Is.EqualTo("1 check failing, 1 check still running"));
	}

	[Test]
	public void BlockedOnAReviewSaysThatInsteadOfTheStatus()
	{
		var state = new MergeState("MERGEABLE", "BLOCKED", ReviewDecision: "REVIEW_REQUIRED",
			BaseRefName: "main", StatusCheckRollup: Checks(("build", "SUCCESS")));

		Assert.That(state.Summary, Is.EqualTo("no approving review yet"));
	}

	[Test]
	public void ConflictsComeBeforeEverythingElse()
	{
		var state = new MergeState("CONFLICTING", "DIRTY", ReviewDecision: "REVIEW_REQUIRED",
			BaseRefName: "main", StatusCheckRollup: Checks(("build", "IN_PROGRESS")));

		Assert.That(state.Summary, Does.StartWith("conflicts with main"));
	}

	[Test]
	public void ABlockedPullRequestWithNothingVisibleSaysThatRatherThanNothing()
	{
		// A required check that never reported, or a code-owner rule this account cannot read:
		// there is genuinely nothing to name, and saying so beats repeating "BLOCKED".
		var state = new MergeState("MERGEABLE", "BLOCKED", BaseRefName: "main");

		Assert.That(state.Summary, Is.EqualTo("blocked by a rule this account cannot read"));
	}

	[Test]
	public void AMergeableOneSaysNothingBlocksIt()
	{
		var state = new MergeState("MERGEABLE", "CLEAN", BaseRefName: "main",
			StatusCheckRollup: Checks(("build", "SUCCESS")));

		Assert.That(state.CanMerge, Is.True);
		Assert.That(state.Summary, Is.EqualTo("nothing blocks it"));
	}

	[Test]
	public void ADraftSaysSoEvenWhenTheStatusDoesNot()
	{
		// A repository with no rule about drafts reports CLEAN for one, so the flag is the
		// only thing that knows.
		var state = new MergeState("MERGEABLE", "CLEAN", IsDraft: true, BaseRefName: "main");

		Assert.That(state.Summary, Is.EqualTo("still a draft"));
	}

	static MergeState Blocked(JsonElement rollup)
		=> new("MERGEABLE", "BLOCKED", BaseRefName: "main", StatusCheckRollup: rollup);

	/// <summary>
	/// A status check rollup shaped the way gh hands one over, which matters: a check that is
	/// still running carries a status and a null conclusion, and it is the empty conclusion
	/// that puts it in the pending bucket. Writing the running state into conclusion instead
	/// would pass without ever exercising that.
	/// </summary>
	static JsonElement Checks(params (string Name, string Conclusion)[] checks)
	{
		string json = "[" + string.Join(",", checks.Select(c => c.Conclusion switch {
			"IN_PROGRESS" or "QUEUED" => $$"""{"__typename":"CheckRun","name":"{{c.Name}}","status":"{{c.Conclusion}}","conclusion":null}""",
			_ => $$"""{"__typename":"CheckRun","name":"{{c.Name}}","status":"COMPLETED","conclusion":"{{c.Conclusion}}"}""",
		})) + "]";
		return JsonDocument.Parse(json).RootElement.Clone();
	}
}
