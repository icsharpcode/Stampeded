using NUnit.Framework;

using Stampeded.Core.GitHub;

namespace Stampeded.Core.Tests;

[TestFixture]
public class PrApprovalTests
{
	static PrSummary Pr(string? decision, params (string Login, string State)[] reviews)
		=> new PrSummary(1, "t", null, "head", "base", false, default, null, null, decision,
			LatestReviews: [.. reviews.Select(r => new PrLatestReview(new PrAuthor(r.Login), r.State))]) {
			ViewerLogin = "me",
		};

	[Test]
	public void ApprovedByTheReaderIsToldApartFromApprovedByAnyone()
	{
		Assert.That(Pr("APPROVED", ("me", "APPROVED")).ApprovedByMe, Is.True);
		Assert.That(Pr("APPROVED", ("me", "APPROVED")).ApprovedByOthers, Is.False);
		Assert.That(Pr("APPROVED", ("someone", "APPROVED")).ApprovedByMe, Is.False);
		Assert.That(Pr("APPROVED", ("someone", "APPROVED")).ApprovedByOthers, Is.True);
	}

	[Test]
	public void AVoteIsReadFromTheReviewsNotFromTheDecision()
	{
		// Approved by the reader while another reviewer still blocks: the decision is not
		// APPROVED, but the reader has had their say.
		var pr = Pr("CHANGES_REQUESTED", ("me", "APPROVED"), ("someone", "CHANGES_REQUESTED"));
		Assert.That(pr.ApprovedByMe, Is.True);
		Assert.That(Pr("APPROVED", ("me", "COMMENTED")).ApprovedByMe, Is.False);
		Assert.That((Pr("APPROVED", ("me", "APPROVED")) with { ViewerLogin = null }).ApprovedByMe, Is.False);
	}

	[Test]
	public void AReviewIsAskedOfTheReaderByNameOnly()
	{
		var asked = Pr(null) with { ReviewRequests = [new PrReviewRequest("me")] };
		Assert.That(asked.ReviewRequestedFromMe, Is.True);
		// A team the reader is in: gh names the team, and nobody in particular is being asked.
		var team = Pr(null) with { ReviewRequests = [new PrReviewRequest(null), new PrReviewRequest("someone")] };
		Assert.That(team.ReviewRequestedFromMe, Is.False);
		Assert.That(Pr(null).ReviewRequestedFromMe, Is.False);
		Assert.That((asked with { ViewerLogin = null }).ReviewRequestedFromMe, Is.False);
	}
}
