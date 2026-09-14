namespace Stampeded.Core.PullRequests;

/// <summary>Marks a review as posted by this tool. The mark is markdown, which every host
/// renders, so it belongs to the review rather than to the host that takes it.</summary>
public static class ReviewAttribution
{
	/// <summary>
	/// Marks a review as posted by this tool, once, on the first thing a reader will meet.
	/// That is the first line comment - the file view, a thread and a mail notification all
	/// show those, and none of them shows the summary the comments were batched into. With no
	/// line comments the summary is what carries it instead.
	///
	/// An approval or a rejection with nothing written at all is left alone: the mark would be
	/// the entire review, which says who ran it and nothing about the change.
	/// </summary>
	public static ReviewSubmission Attributed(ReviewSubmission submission)
	{
		if (submission.Comments.Count > 0)
		{
			return submission with {
				Comments = [
					submission.Comments[0] with { Body = WithAttribution(submission.Comments[0].Body) },
					.. submission.Comments.Skip(1),
				],
			};
		}
		return submission.Body.Trim().Length == 0
			? submission
			: submission with { Body = WithAttribution(submission.Body) };
	}

	/// <summary>The mark for a pass that is nothing but replies: those are posted one by one
	/// rather than as a review, so the review body that would otherwise carry it is never
	/// sent, and the first reply is the first thing a reader will meet.</summary>
	public static string AttributedReply(string body) => WithAttribution(body);

	static string WithAttribution(string body)
		=> (body.Length > 0 ? body.TrimEnd() + "\n\n" : "")
			+ "*Reviewed with [Stampeded!](https://github.com/icsharpcode/Stampeded)*";
}
