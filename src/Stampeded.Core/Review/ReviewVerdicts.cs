using Stampeded.Core.GitHub;

namespace Stampeded.Core.Review;

/// <summary>Where one reviewer stands, and on which head they said it.</summary>
public sealed record ReviewVerdict(string Author, bool Approved, string? CommitId);

/// <summary>
/// Who has approved a pull request and who has asked for changes. GitHub keeps every review
/// ever submitted; what counts is where each person stands now.
/// </summary>
public static class ReviewVerdicts
{
	/// <summary>
	/// The standing of each reviewer: their most recent review that took a position. A comment
	/// takes none - it leaves an earlier approval or request in force - and a dismissed
	/// approval is withdrawn, so that reviewer is back to having said nothing. Ordered by name,
	/// because a list that reorders itself as people revisit a review is read as new activity.
	/// </summary>
	public static IReadOnlyList<ReviewVerdict> Latest(IReadOnlyList<PrReview> reviews)
	{
		var standing = new Dictionary<string, ReviewVerdict?>(StringComparer.OrdinalIgnoreCase);
		foreach (var review in reviews.OrderBy(r => r.SubmittedAt ?? DateTimeOffset.MinValue))
		{
			if (review.User?.Login is not { Length: > 0 } author)
				continue;
			switch (review.State?.ToUpperInvariant())
			{
				case "APPROVED":
					standing[author] = new ReviewVerdict(author, Approved: true, review.CommitId);
					break;
				case "CHANGES_REQUESTED":
					standing[author] = new ReviewVerdict(author, Approved: false, review.CommitId);
					break;
				case "DISMISSED":
					standing[author] = null;
					break;
			}
		}
		return [.. standing.Values.OfType<ReviewVerdict>().OrderBy(v => v.Author, StringComparer.OrdinalIgnoreCase)];
	}
}
