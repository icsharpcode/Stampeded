namespace Stampeded.Core.PullRequests;

/// <summary>
/// Every pull-request fact a review needs, from whichever host the repository is on. GitHub's
/// vocabulary is the vocabulary of the model - APPROVE / REQUEST_CHANGES / COMMENT, APPROVED /
/// CHANGES_REQUESTED, LEFT / RIGHT, MERGEABLE / BLOCKED - because the panes and the pure
/// functions under them already speak it; another host maps onto it in its own implementation
/// and nowhere else.
/// </summary>
public interface IPullRequestHost
{
	/// <summary>"GitHub" / "Azure DevOps", for every status line, dialog and menu that names it.</summary>
	string Name { get; }

	/// <summary>Whether the host takes an approval from the pull request's own author. GitHub
	/// refuses it, Azure DevOps does not.</summary>
	bool AcceptsOwnApproval { get; }

	/// <summary>The refspec that fetches pull request <paramref name="number"/>'s head from
	/// origin into refs/stampeded/pr/N.</summary>
	Task<string> PrHeadRefspecAsync(int number, CancellationToken ct = default);

	/// <summary>Where a browser would show the pull request, or null when the repository is not
	/// on this host - a review of a local branch in a clone with no such remote.</summary>
	Task<string?> PrUrlAsync(int number, CancellationToken ct = default);

	/// <summary>Where a browser would show one commit, or null as above.</summary>
	Task<string?> CommitUrlAsync(string sha, CancellationToken ct = default);

	Task<string> GetViewerLoginAsync(CancellationToken ct = default);
	Task<string> GetDefaultBranchAsync(CancellationToken ct = default);
	Task<IReadOnlyList<PrSummary>> ListOpenPrsAsync(CancellationToken ct = default);
	Task<PrDetail> GetPrAsync(int number, CancellationToken ct = default);
	Task<IReadOnlyList<CheckRun>> GetChecksAsync(int number, CancellationToken ct = default);
	Task<MergeState> GetMergeStateAsync(int number, CancellationToken ct = default);
	Task<string?> GetIssueTitleAsync(int number, CancellationToken ct = default);
	Task<string?> GetIssueUrlPrefixAsync(CancellationToken ct = default);
	Task<MergeMethods> GetMergeMethodsAsync(CancellationToken ct = default);
	Task<string> MergePrAsync(int number, string method, bool deleteBranch = false, CancellationToken ct = default);
	Task<string> MarkReadyForReviewAsync(int number, CancellationToken ct = default);

	/// <summary>Log lines of the failed steps of one run: GitHub Actions' run id, Azure DevOps'
	/// build id, as <see cref="CheckRun.RunId"/> carries it.</summary>
	Task<string> GetFailedLogAsync(long runId, CancellationToken ct = default);

	Task<IReadOnlyList<PostedComment>> GetReviewCommentsAsync(int number, CancellationToken ct = default);
	Task<IReadOnlyList<PrReview>> GetReviewsAsync(int number, CancellationToken ct = default);
	Task<IReadOnlyList<ThreadResolution>> GetThreadResolutionsAsync(int number, CancellationToken ct = default);
	Task SetThreadResolvedAsync(string threadId, bool resolved, CancellationToken ct = default);

	/// <summary>Rebases the pull request's branch onto its target on the server. A host without
	/// such an API throws <see cref="Infra.RefusedException"/> saying so.</summary>
	Task UpdateBranchAsync(int number, CancellationToken ct = default);

	Task SubmitReviewAsync(int number, ReviewSubmission submission, CancellationToken ct = default);
	Task ReplyToCommentAsync(int number, long commentId, string body, CancellationToken ct = default);

	/// <summary>Whether something on the host empties the merge queue without a reader's window
	/// being open - GitHub's drainer workflow. A host with no such thing answers false, and the
	/// queue is drained by whoever has the window open, as it is on a repository without one.</summary>
	Task<bool> HasMergeQueueWorkflowAsync(CancellationToken ct = default);

	/// <summary>Tells that drainer there is something to do. Does nothing where there is none.</summary>
	Task DispatchMergeQueueAsync(CancellationToken ct = default);
}
