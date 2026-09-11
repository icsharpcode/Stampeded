using System.Text.Json.Serialization;

namespace Stampeded.Core.PullRequests;

public sealed record PrAuthor(string Login);

/// <summary>One reviewer's last word on a pull request, as `latestReviews` hands it over.</summary>
public sealed record PrLatestReview(PrAuthor? Author, string? State);

/// <summary>The account a repository belongs to, as `headRepositoryOwner` hands it over.</summary>
public sealed record PrRepoOwner(string Login);

/// <summary>Someone a review has been asked of. A team has no login, which is why this is
/// not a <see cref="PrAuthor"/>: `reviewRequests` holds both.</summary>
public sealed record PrReviewRequest(string? Login);

public sealed record PrSummary(
	int Number,
	string Title,
	PrAuthor? Author,
	string HeadRefName,
	string BaseRefName,
	bool IsDraft,
	DateTimeOffset UpdatedAt,
	System.Text.Json.JsonElement? StatusCheckRollup = null,
	string? HeadRefOid = null,
	string? ReviewDecision = null,
	int Additions = 0,
	int Deletions = 0,
	int ChangedFiles = 0,
	IReadOnlyList<PrLatestReview>? LatestReviews = null,
	IReadOnlyList<PrReviewRequest>? ReviewRequests = null,
	PrRepoOwner? HeadRepositoryOwner = null)
{
	/// <summary>The login gh is authenticated as, stamped on after the list is read: only
	/// that tells "approved" apart from "approved by the reader".</summary>
	public string? ViewerLogin { get; init; }

	/// <summary>The owner origin belongs to, stamped on after the list is read: a head branch
	/// name means nothing without it, because a pull request lists the branch as it is named
	/// in the repository it lives in, which for a fork is not this one.</summary>
	public string? OriginOwner { get; init; }

	/// <summary>The head branch is in somebody's fork, so no branch of this clone is that
	/// branch however alike the two are named - "master" from a fork is not the master that
	/// is checked out here. Owner alone decides it: a fork cannot sit beside its original
	/// under the same account.</summary>
	public bool HeadIsFork => OriginOwner is { Length: > 0 } origin
		&& HeadRepositoryOwner is { Login.Length: > 0 } head
		&& !string.Equals(head.Login, origin, StringComparison.OrdinalIgnoreCase);

	/// <summary>"fail" / "pending" / "green" / "none", folded from the check rollup.</summary>
	public string ChecksBucket => CheckRollup.Bucket(StatusCheckRollup);

	public bool ChecksFailed => ChecksBucket == "fail";
	public bool ChecksPending => ChecksBucket == "pending";
	public bool ChecksGreen => ChecksBucket == "green";

	public bool IsApproved => ReviewDecision == "APPROVED";
	public bool ChangesRequested => ReviewDecision == "CHANGES_REQUESTED";

	/// <summary>The reader's own last review approved this. A pull request can be approved
	/// without their vote, and voted on without the pull request being approved, so this is
	/// read from the reviews rather than from the decision.</summary>
	public bool ApprovedByMe => ViewerLogin is { Length: > 0 } me
		&& LatestReviews?.Any(r => r.State == "APPROVED"
			&& string.Equals(r.Author?.Login, me, StringComparison.OrdinalIgnoreCase)) == true;

	/// <summary>A review has been asked of the reader by name. Only of them: a request sent
	/// to a team they are in is a request nobody in particular has to answer, and gh names the
	/// team rather than its members.</summary>
	public bool ReviewRequestedFromMe => ViewerLogin is { Length: > 0 } me
		&& ReviewRequests?.Any(r => string.Equals(r.Login, me, StringComparison.OrdinalIgnoreCase)) == true;

	/// <summary>Approved, but not by the reader - so the two badges never both show.</summary>
	public bool ApprovedByOthers => IsApproved && !ApprovedByMe;

	public string NumberDisplay => $"#{Number}";

	/// <summary>The size of the change, as GitHub counts it. Kept to the line totals: this
	/// shares a line with the branches, and the file count is in the tooltip.</summary>
	public string AddedDisplay => $"+{Additions}";

	public string RemovedDisplay => $"-{Deletions}";

	/// <summary>The whole of the branch line, for when the column is too narrow to show it.</summary>
	public string BranchesTip => $"{HeadRefName} -> {BaseRefName}, by {Author?.Login ?? "unknown"}";

	public string StatsTip => $"{ChangedFiles} changed file(s), {Additions} line(s) added, "
		+ $"{Deletions} removed, as GitHub counts them";
}

public sealed record PrDetail(
	int Number,
	string Title,
	string? Body,
	string BaseRefName,
	string HeadRefName,
	string State,
	PrAuthor? Author,
	bool IsDraft = false);

/// <summary>One check on a pull request. <see cref="RunId"/> names the run whose failed log
/// can be fetched - GitHub Actions' run id, Azure DevOps' build id - and is null for a check
/// that reports from somewhere neither can read.</summary>
public sealed record CheckRun(string Name, string State, string Bucket, string? Link, string? Workflow,
	long? RunId = null);

/// <summary>
/// A pull request's status-check rollup as GitHub hands it over: check runs carry status and
/// conclusion, the older status contexts carry state, and both kinds arrive in one list. Read
/// in one place because the pull request list and the merge state fold it the same way, and
/// two foldings that drift apart would have the same review reported green in one pane and
/// failing in another.
/// </summary>
public static class CheckRollup
{
	/// <summary>"fail", "pending" or "green" for one entry. Anything not named is green:
	/// SUCCESS, but also SKIPPED and NEUTRAL, which are not a check saying no.</summary>
	public static string Verdict(System.Text.Json.JsonElement item)
	{
		string? conclusion = item.TryGetProperty("conclusion", out var c) ? c.GetString() : null;
		string? state = item.TryGetProperty("state", out var s) ? s.GetString() : null;
		return ((conclusion is { Length: > 0 } ? conclusion : state) ?? "").ToUpperInvariant() switch {
			"FAILURE" or "ERROR" or "TIMED_OUT" or "STARTUP_FAILURE" or "CANCELLED" or "ACTION_REQUIRED" => "fail",
			"" or "PENDING" or "IN_PROGRESS" or "QUEUED" or "EXPECTED" or "WAITING" or "REQUESTED" => "pending",
			_ => "green",
		};
	}

	/// <summary>"fail" / "pending" / "green" / "none" for the whole rollup, worst first.</summary>
	public static string Bucket(System.Text.Json.JsonElement? rollup)
	{
		bool pending = false;
		foreach (var item in Entries(rollup))
		{
			switch (Verdict(item))
			{
				case "fail":
					return "fail";
				case "pending":
					pending = true;
					break;
			}
		}
		return pending ? "pending" : Entries(rollup).Any() ? "green" : "none";
	}

	/// <summary>The checks with one verdict, named, in the order GitHub listed them.</summary>
	public static IReadOnlyList<string> Names(System.Text.Json.JsonElement? rollup, string verdict)
		=> [.. Entries(rollup).Where(item => Verdict(item) == verdict).Select(Name)];

	static string Name(System.Text.Json.JsonElement item)
		=> (item.TryGetProperty("name", out var name) ? name.GetString() : null)
			?? (item.TryGetProperty("context", out var context) ? context.GetString() : null)
			?? "(unnamed check)";

	static IEnumerable<System.Text.Json.JsonElement> Entries(System.Text.Json.JsonElement? rollup)
		=> rollup is { ValueKind: System.Text.Json.JsonValueKind.Array } array
			? array.EnumerateArray()
			: [];
}

/// <summary>
/// Whether GitHub would take a merge of this pull request right now, in its own words:
/// <see cref="Mergeable"/> is MERGEABLE / CONFLICTING / UNKNOWN, <see cref="MergeStateStatus"/>
/// is CLEAN, UNSTABLE, BLOCKED, BEHIND, DIRTY, DRAFT, HAS_HOOKS or UNKNOWN.
/// </summary>
public sealed record MergeState(
	string? Mergeable,
	string? MergeStateStatus,
	string? ReviewDecision = null,
	bool IsDraft = false,
	string? BaseRefName = null,
	System.Text.Json.JsonElement? StatusCheckRollup = null,
	string? State = null,
	string? HeadRefOid = null)
{
	/// <summary>The host these two words came from, for the lines that quote it by name.</summary>
	public string Host { get; init; } = "GitHub";

	/// <summary>
	/// UNSTABLE is a failing or pending check on a pull request GitHub would still merge, so
	/// it is the reader's call and not a refusal. BLOCKED, BEHIND and DIRTY are refusals whose
	/// remedy is not a merge; UNKNOWN is what GitHub answers without push access, and offering
	/// a button that will be rejected is worse than not offering one.
	/// </summary>
	public bool CanMerge => Mergeable == "MERGEABLE"
		&& MergeStateStatus is "CLEAN" or "UNSTABLE" or "HAS_HOOKS";

	/// <summary>GitHub's own two words, for the line that quotes it rather than reads it.</summary>
	public string Describe => $"{Mergeable ?? "UNKNOWN"} / {MergeStateStatus ?? "UNKNOWN"}";

	/// <summary>
	/// What is actually in the way, in the fewest words that let the reader decide what to do.
	///
	/// "MERGEABLE / BLOCKED" is GitHub answering a different question: it names the kind of
	/// refusal, not the thing to wait for or fix, and the two most common reasons behind it -
	/// a check still running and a review not given - are indistinguishable in it. Both are
	/// in the fields alongside, so they are read here and the raw pair is kept for the tooltip.
	///
	/// Ordered by what the reader would do about it: what they must fix first, then what they
	/// are waiting on, then what somebody else owes them.
	/// </summary>
	public string Summary
	{
		get
		{
			string target = BaseRefName is { Length: > 0 } ? BaseRefName : "the target branch";
			string status = MergeStateStatus is { Length: > 0 } ? MergeStateStatus : "UNKNOWN";
			var reasons = new List<string>();
			if (Mergeable == "CONFLICTING" || status == "DIRTY")
				reasons.Add($"conflicts with {target}");
			if (IsDraft || status == "DRAFT")
				reasons.Add("still a draft");
			if (status == "BEHIND")
				reasons.Add($"behind {target}");
			if (CheckRollup.Names(StatusCheckRollup, "fail") is { Count: > 0 } failing)
				reasons.Add($"{failing.Count} check{(failing.Count == 1 ? "" : "s")} failing");
			if (CheckRollup.Names(StatusCheckRollup, "pending") is { Count: > 0 } running)
				reasons.Add($"{running.Count} check{(running.Count == 1 ? "" : "s")} still running");
			if (ReviewDecision == "CHANGES_REQUESTED")
				reasons.Add("changes requested");
			else if (ReviewDecision == "REVIEW_REQUIRED")
				reasons.Add("no approving review yet");

			if (reasons.Count > 0)
				// Two at most: a third is detail the tooltip already carries in full.
				return string.Join(", ", reasons.Take(2));
			if (status == "BLOCKED")
				return "blocked by a rule this account cannot read";
			if (status == "UNKNOWN")
				return $"{Host} has not worked it out yet";
			return CanMerge ? "nothing blocks it" : Describe;
		}
	}

	/// <summary>
	/// Why the merge would be refused, in as much detail as GitHub gives from here. Its two
	/// words say the kind of refusal; what the reader needs is which of the several things
	/// behind that word is missing, and GitHub answers that in other fields - the review
	/// decision, the checks, the draft flag - which are read here alongside it.
	///
	/// BLOCKED is the one it will not always explain: a rule can require a check that has not
	/// reported at all, or a code-owner review, and neither shows up in what a reader can see.
	/// Saying so is better than listing nothing and looking broken.
	/// </summary>
	public string Explain
	{
		get
		{
			string target = BaseRefName is { Length: > 0 } ? BaseRefName : "the target branch";
			// A field GitHub left out is a state it does not know, which is what UNKNOWN means.
			string status = MergeStateStatus is { Length: > 0 } ? MergeStateStatus : "UNKNOWN";
			var lines = new List<string> { $"{Host} says: {Describe}." };
			if (Mergeable == "CONFLICTING" || MergeStateStatus == "DIRTY")
			{
				lines.Add($"The branch conflicts with {target}. Rebase it onto {target}, or merge "
					+ $"{target} into it, and push.");
			}
			switch (status)
			{
				case "BEHIND":
					lines.Add($"The branch is behind {target}, and this repository requires it to be "
						+ "up to date before a merge. Rebase it and push.");
					break;
				case "DRAFT":
					lines.Add("The pull request is a draft. It has to be marked ready for review.");
					break;
				case "BLOCKED":
					lines.Add($"A branch protection rule on {target} refuses it.");
					break;
				case "UNSTABLE":
					lines.Add($"{Host} would take it as it is; a check is failing or has not finished, "
						+ "and whether that matters is the reader's call.");
					break;
				case "UNKNOWN":
					lines.Add($"{Host} has not worked the state out yet, or this account has no push "
						+ "access to the repository. Refreshing in a moment usually answers it.");
					break;
			}
			var reasons = new List<string>();
			if (ReviewDecision == "REVIEW_REQUIRED")
				reasons.Add("No approving review yet.");
			else if (ReviewDecision == "CHANGES_REQUESTED")
				reasons.Add("A review has requested changes.");
			if (CheckRollup.Names(StatusCheckRollup, "fail") is { Count: > 0 } failing)
				reasons.Add($"Checks failing: {string.Join(", ", failing)}.");
			if (CheckRollup.Names(StatusCheckRollup, "pending") is { Count: > 0 } running)
				reasons.Add($"Checks not finished: {string.Join(", ", running)}.");
			if (IsDraft && status != "DRAFT")
				reasons.Add("The pull request is a draft.");
			if (reasons.Count == 0 && status == "BLOCKED")
			{
				reasons.Add("Which rule is not visible from here: a required check that has not "
					+ "reported, a review from a code owner, or a rule this account cannot read.");
			}
			lines.AddRange(reasons.Select(r => "- " + r));
			if (CanMerge && reasons.Count == 0 && status is "CLEAN" or "HAS_HOOKS")
				lines.Add("Nothing blocks it.");
			return string.Join("\n", lines);
		}
	}
}

/// <summary>The merge methods the repository's settings allow.</summary>
public sealed record MergeMethods(bool MergeCommitAllowed, bool SquashMergeAllowed, bool RebaseMergeAllowed)
{
	/// <summary>The gh flags for the allowed methods, in the order GitHub's own menu lists them.</summary>
	public IReadOnlyList<string> Allowed
	{
		get
		{
			var methods = new List<string>();
			if (MergeCommitAllowed)
				methods.Add("merge");
			if (SquashMergeAllowed)
				methods.Add("squash");
			if (RebaseMergeAllowed)
				methods.Add("rebase");
			return methods;
		}
	}
}

public sealed record PostedUser(string Login);

public sealed record PostedComment(
	long Id,
	string Body,
	string Path,
	int? Line,
	string? Side,
	PostedUser? User,
	[property: JsonPropertyName("original_line")] int? OriginalLine,
	[property: JsonPropertyName("diff_hunk")] string? DiffHunk,
	/// <summary>The commit the comment was written against. Still in the object database
	/// whenever that head was ever fetched, which is what lets the code it was about be read
	/// as it was.</summary>
	[property: JsonPropertyName("original_commit_id")] string? OriginalCommitId,
	[property: JsonPropertyName("html_url")] string? HtmlUrl = null);

/// <summary>One submitted review of a pull request: who, what they said of it, and the head
/// they said it of - which is not always the one on screen.</summary>
public sealed record PrReview(
	PostedUser? User,
	string? State,
	[property: JsonPropertyName("commit_id")] string? CommitId,
	[property: JsonPropertyName("submitted_at")] DateTimeOffset? SubmittedAt);

/// <summary>Resolution state of one GitHub review thread and the REST ids of its comments.</summary>
public sealed record ThreadResolution(string ThreadId, bool IsResolved, IReadOnlyList<long> CommentIds);

public sealed record ReviewCommentDto(string Path, int Line, string Side, string Body);

public sealed record ReviewSubmission(string Body, string Event, IReadOnlyList<ReviewCommentDto> Comments);

/// <summary>The whole payload of a reply: the thread it joins is named by the URL.</summary>
public sealed record ReplyBody(string Body);
