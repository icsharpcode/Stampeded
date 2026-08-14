using NUnit.Framework;

using Stampeded.Core.GitHub;

namespace Stampeded.Core.Tests;

public class ReviewAttributionTests
{
	const string Mark = "*Reviewed with [Stampeded!](https://github.com/icsharpcode/Stampeded)*";

	static ReviewCommentDto Comment(string body) => new("a.cs", 1, "RIGHT", body);

	[Test]
	public void MarksOnlyTheFirstCommentOfAReview()
	{
		var submitted = GitHubService.Attributed(
			new ReviewSubmission("", "COMMENT", [Comment("first"), Comment("second"), Comment("third")]));

		Assert.That(submitted.Comments[0].Body, Is.EqualTo("first\n\n" + Mark));
		Assert.That(submitted.Comments[1].Body, Is.EqualTo("second"));
		Assert.That(submitted.Comments[2].Body, Is.EqualTo("third"));
		// The summary stays as written; the comment carries the mark.
		Assert.That(submitted.Body, Is.EqualTo(""));
	}

	[Test]
	public void MarksTheBodyWhenAReviewHasNoLineComments()
	{
		var approval = GitHubService.Attributed(new ReviewSubmission("", "APPROVE", []));
		Assert.That(approval.Body, Is.EqualTo(Mark));

		var withSummary = GitHubService.Attributed(new ReviewSubmission("Looks good.", "APPROVE", []));
		Assert.That(withSummary.Body, Is.EqualTo("Looks good.\n\n" + Mark));
	}
}
