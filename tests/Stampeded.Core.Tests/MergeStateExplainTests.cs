using System.Text.Json;

using NUnit.Framework;

using Stampeded.Core.GitHub;

namespace Stampeded.Core.Tests;

/// <summary>
/// What the review view says when GitHub refuses a merge. Its two words name the kind of
/// refusal; these cases pin that the reason behind the word is spelled out where GitHub gives
/// it, and admitted where it does not.
/// </summary>
public class MergeStateExplainTests
{
	static JsonElement Rollup(params (string Name, string Conclusion)[] checks)
		=> JsonDocument.Parse(JsonSerializer.Serialize(
			checks.Select(c => new { name = c.Name, conclusion = c.Conclusion }))).RootElement.Clone();

	[Test]
	public void NamesTheChecksAndTheMissingReviewBehindABlock()
	{
		var state = new MergeState("MERGEABLE", "BLOCKED", "REVIEW_REQUIRED", false, "main",
			Rollup(("build", "FAILURE"), ("docs", "IN_PROGRESS"), ("lint", "SUCCESS")));

		string explanation = state.Explain;

		Assert.That(state.CanMerge, Is.False);
		Assert.That(explanation, Does.Contain("branch protection rule on main"));
		Assert.That(explanation, Does.Contain("No approving review yet."));
		Assert.That(explanation, Does.Contain("Checks failing: build."));
		Assert.That(explanation, Does.Contain("Checks not finished: docs."));
		Assert.That(explanation, Does.Not.Contain("lint"), "a check that passed is not a reason");
	}

	[Test]
	public void AdmitsWhenGitHubWillNotSayWhatTheRuleIs()
	{
		// Everything visible is fine and it is still blocked: a required check that never
		// reported, or a code-owner review. Listing nothing would read as a broken tooltip.
		var state = new MergeState("MERGEABLE", "BLOCKED", "APPROVED", false, "main",
			Rollup(("build", "SUCCESS")));

		Assert.That(state.Explain, Does.Contain("not visible from here"));
	}

	[Test]
	public void TellsAConflictFromABranchThatIsMerelyBehind()
	{
		Assert.That(new MergeState("CONFLICTING", "DIRTY", BaseRefName: "main").Explain,
			Does.Contain("conflicts with main"));
		Assert.That(new MergeState("MERGEABLE", "BEHIND", BaseRefName: "main").Explain,
			Does.Contain("behind main"));
	}

	[Test]
	public void SaysADraftIsADraftAndAnUnknownStateIsNotAnAnswer()
	{
		Assert.That(new MergeState("MERGEABLE", "DRAFT").Explain, Does.Contain("draft"));
		Assert.That(new MergeState(null, null).Explain, Does.Contain("no push access"));
	}

	[Test]
	public void SaysNothingBlocksACleanOne()
	{
		var state = new MergeState("MERGEABLE", "CLEAN", "APPROVED", false, "main", Rollup(("build", "SUCCESS")));

		Assert.That(state.CanMerge, Is.True);
		Assert.That(state.Explain, Does.Contain("Nothing blocks it."));
	}

	[Test]
	public void CallsAFailingCheckOutOnAPullRequestGitHubWouldStillTake()
	{
		// UNSTABLE is mergeable with a check that did not pass: the button stays live, and the
		// tooltip is where the reader learns what they would be merging over.
		var state = new MergeState("MERGEABLE", "UNSTABLE", "APPROVED", false, "main",
			Rollup(("flaky", "FAILURE")));

		Assert.That(state.CanMerge, Is.True);
		Assert.That(state.Explain, Does.Contain("Checks failing: flaky."));
		Assert.That(state.Explain, Does.Contain("reader's call"));
	}
}
