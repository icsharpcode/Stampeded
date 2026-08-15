using NUnit.Framework;

using Stampeded.Core.GitHub;
using Stampeded.Core.Review;

namespace Stampeded.Core.Tests;

public class ReviewVerdictTests
{
	static PrReview Review(string author, string state, string commit = "sha", int minute = 0)
		=> new(new PostedUser(author), state, commit, new DateTimeOffset(2026, 1, 1, 12, minute, 0, TimeSpan.Zero));

	[Test]
	public void TakesEachReviewersMostRecentPosition()
	{
		var verdicts = ReviewVerdicts.Latest([
			Review("alice", "CHANGES_REQUESTED", minute: 1),
			Review("bob", "APPROVED", minute: 2),
			Review("alice", "APPROVED", "newer", minute: 3),
		]);

		Assert.That(verdicts.Select(v => v.Author), Is.EqualTo(new[] { "alice", "bob" }));
		Assert.That(verdicts[0].Approved, Is.True);
		Assert.That(verdicts[0].CommitId, Is.EqualTo("newer"));
	}

	[Test]
	public void LeavesAPositionStandingWhenTheSamePersonOnlyComments()
	{
		// A comment says nothing about whether the change should go in, so it does not
		// withdraw what was said before it.
		var verdicts = ReviewVerdicts.Latest([
			Review("alice", "APPROVED", minute: 1),
			Review("alice", "COMMENTED", minute: 2),
		]);

		Assert.That(verdicts, Has.Count.EqualTo(1));
		Assert.That(verdicts[0].Approved, Is.True);
	}

	[Test]
	public void ForgetsAReviewerWhoseApprovalWasDismissed()
	{
		var verdicts = ReviewVerdicts.Latest([
			Review("alice", "APPROVED", minute: 1),
			Review("alice", "DISMISSED", minute: 2),
		]);

		Assert.That(verdicts, Is.Empty);
	}

	[Test]
	public void IgnoresReviewsWithNobodyBehindThem()
	{
		var verdicts = ReviewVerdicts.Latest([new PrReview(null, "APPROVED", "sha", null)]);

		Assert.That(verdicts, Is.Empty);
	}
}
