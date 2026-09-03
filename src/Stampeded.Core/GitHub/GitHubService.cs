using CliWrap.Buffered;

using System.Text.Json;
using System.Text.Json.Serialization;

using Stampeded.Core.Infra;

namespace Stampeded.Core.GitHub;

public sealed record PrAuthor(string Login);

/// <summary>One reviewer's last word on a pull request, as `latestReviews` hands it over.</summary>
public sealed record PrLatestReview(PrAuthor? Author, string? State);

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
	IReadOnlyList<PrReviewRequest>? ReviewRequests = null)
{
	/// <summary>The login gh is authenticated as, stamped on after the list is read: only
	/// that tells "approved" apart from "approved by the reader".</summary>
	public string? ViewerLogin { get; init; }

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

public sealed record CheckRun(string Name, string State, string Bucket, string? Link, string? Workflow);

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
	/// <summary>
	/// UNSTABLE is a failing or pending check on a pull request GitHub would still merge, so
	/// it is the reader's call and not a refusal. BLOCKED, BEHIND and DIRTY are refusals whose
	/// remedy is not a merge; UNKNOWN is what GitHub answers without push access, and offering
	/// a button that will be rejected is worse than not offering one.
	/// </summary>
	public bool CanMerge => Mergeable == "MERGEABLE"
		&& MergeStateStatus is "CLEAN" or "UNSTABLE" or "HAS_HOOKS";

	public string Describe => $"{Mergeable ?? "UNKNOWN"} / {MergeStateStatus ?? "UNKNOWN"}";

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
			var lines = new List<string> { $"GitHub says: {Describe}." };
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
					lines.Add("GitHub would take it as it is; a check is failing or has not finished, "
						+ "and whether that matters is the reader's call.");
					break;
				case "UNKNOWN":
					lines.Add("GitHub has not worked the state out yet, or this account has no push "
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

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(IReadOnlyList<PrSummary>))]
[JsonSerializable(typeof(PrDetail))]
[JsonSerializable(typeof(IReadOnlyList<CheckRun>))]
[JsonSerializable(typeof(MergeState))]
[JsonSerializable(typeof(MergeMethods))]
[JsonSerializable(typeof(IReadOnlyList<PostedComment>))]
[JsonSerializable(typeof(IReadOnlyList<PrReview>))]
[JsonSerializable(typeof(ReviewSubmission))]
[JsonSerializable(typeof(ReplyBody))]
partial class GitHubJsonContext : JsonSerializerContext
{
}

/// <summary>
/// GitHub access through the `gh` CLI, run in the repository directory so gh resolves
/// the repo from origin. Auth, SSO and token refresh ride on the user's gh login.
/// </summary>
public sealed class GitHubService(string repoPath)
{
	static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
		TypeInfoResolver = GitHubJsonContext.Default,
	};

	string? viewerLogin;
	string? defaultBranch;
	MergeMethods? mergeMethods;

	async Task<T> JsonAsync<T>(CancellationToken ct, params string[] args)
	{
		string output = await ExternalTool.RunAsync("gh", args, repoPath, ct);
		return JsonSerializer.Deserialize<T>(output, JsonOptions)
			?? throw new InvalidOperationException($"gh returned null JSON for: {string.Join(' ', args)}");
	}

	/// <summary>The login gh is authenticated as. Cached: it cannot change while the app
	/// runs without gh being re-authenticated underneath it.</summary>
	public async Task<string> GetViewerLoginAsync(CancellationToken ct = default)
		=> viewerLogin ??= (await ExternalTool.RunAsync("gh", ["api", "user", "--jq", ".login"], repoPath, ct)).Trim();

	/// <summary>The repository's default branch, as GitHub has it. This is the authority:
	/// the local `origin/HEAD` that git offers instead is written at clone time and is not
	/// updated when the repository is renamed or its default is changed.</summary>
	public async Task<string> GetDefaultBranchAsync(CancellationToken ct = default)
		=> defaultBranch ??= (await ExternalTool.RunAsync(
			"gh", ["repo", "view", "--json", "defaultBranchRef", "--jq", ".defaultBranchRef.name"], repoPath, ct)).Trim();

	public Task<IReadOnlyList<PrSummary>> ListOpenPrsAsync(CancellationToken ct = default)
		=> JsonAsync<IReadOnlyList<PrSummary>>(ct,
			"pr", "list",
			"--json", "number,title,author,headRefName,baseRefName,isDraft,updatedAt,statusCheckRollup,headRefOid,reviewDecision,additions,deletions,changedFiles,latestReviews,reviewRequests",
			"--limit", "50");

	public Task<PrDetail> GetPrAsync(int number, CancellationToken ct = default)
		=> JsonAsync<PrDetail>(ct,
			"pr", "view", number.ToString(),
			// isDraft rides along because it decides what the reader may do with the pull
			// request, not merely how it merges: a draft cannot be queued and can be marked
			// ready, and asking GitHub again for one flag would be a round trip for nothing.
			"--json", "number,title,body,baseRefName,headRefName,state,author,isDraft");

	/// <summary>Check runs for a PR. `gh pr checks` exits non-zero when checks failed or
	/// are pending, so the JSON is taken from stdout regardless of exit code.</summary>
	public async Task<IReadOnlyList<CheckRun>> GetChecksAsync(int number, CancellationToken ct = default)
	{
		var result = await CliWrap.Cli.Wrap("gh")
			.WithArguments(["pr", "checks", number.ToString(), "--json", "name,state,link,bucket,workflow"])
			.WithWorkingDirectory(repoPath)
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		CliLog.Write("gh", $"pr checks {number} -> exit {result.ExitCode}");
		if (string.IsNullOrWhiteSpace(result.StandardOutput))
		{
			if (result.ExitCode != 0)
				throw new ToolFailedException("gh", result.ExitCode, result.StandardError);
			return [];
		}
		return JsonSerializer.Deserialize<IReadOnlyList<CheckRun>>(result.StandardOutput, JsonOptions) ?? [];
	}

	/// <summary>What GitHub says about merging this pull request right now. Not cached: it
	/// changes with every push to either branch and with every review someone else leaves.</summary>
	public Task<MergeState> GetMergeStateAsync(int number, CancellationToken ct = default)
		=> JsonAsync<MergeState>(ct, "pr", "view", number.ToString(),
			// The fields beyond the two verdicts are what turns "BLOCKED" into a reason: the
			// review decision, the checks and the draft flag are where GitHub keeps the detail.
			// state and headRefOid are what a merge queue needs on top: whether somebody has
			// merged or closed it since, and whether the branch still carries the revision that
			// was queued.
			"--json", "mergeable,mergeStateStatus,reviewDecision,isDraft,baseRefName,statusCheckRollup,state,headRefOid");

	/// <summary>
	/// The title of an issue or pull request of this repository, or null when the number is
	/// not one of either. A number in a description is only a reference if something answers
	/// to it: "#141414" is a colour and "#0" is nothing, and both read as issues until asked.
	/// Answers are kept for the session - an issue's existence does not change under a review,
	/// and the same description is rebuilt on every refresh.
	/// </summary>
	public Task<string?> GetIssueTitleAsync(int number, CancellationToken ct = default)
	{
		// The question in flight is what is kept, not only its answer: a page rebuilt twice in
		// a row would otherwise ask twice about every number before either reply arrived.
		if (!issueTitles.TryGetValue(number, out var pending))
			issueTitles[number] = pending = AskAsync();
		return pending;

		async Task<string?> AskAsync()
		{
			var result = await CliWrap.Cli.Wrap("gh")
				.WithArguments(["api", $"repos/{{owner}}/{{repo}}/issues/{number}", "--jq", ".title"])
				.WithWorkingDirectory(repoPath)
				.WithValidation(CliWrap.CommandResultValidation.None)
				.ExecuteBufferedAsync(ct);
			// A 404 is the answer, not a failure: it says the number is not an issue.
			string? title = result.ExitCode == 0 && result.StandardOutput.Trim() is { Length: > 0 } text
				? text
				: null;
			CliLog.Write("gh", $"issue {number} -> {(title is null ? "not an issue" : "exists")}");
			return title;
		}
	}

	/// <summary>Asked once per number and kept for the session; the callers are on the UI
	/// thread, which is what makes an unlocked dictionary enough.</summary>
	readonly Dictionary<int, Task<string?>> issueTitles = [];

	/// <summary>The merge methods the repository allows; a setting, so it is read once.</summary>
	public async Task<MergeMethods> GetMergeMethodsAsync(CancellationToken ct = default)
		=> mergeMethods ??= await JsonAsync<MergeMethods>(ct,
			"repo", "view", "--json", "mergeCommitAllowed,squashMergeAllowed,rebaseMergeAllowed");

	/// <summary>
	/// Whether this repository has the drainer workflow installed, so the queue empties itself
	/// without a reader's window being open. Asked once: a workflow is not added mid-session, and
	/// a repository with no Actions, or a login without the rights to list them, simply has none.
	/// </summary>
	public async Task<bool> HasMergeQueueWorkflowAsync(CancellationToken ct = default)
	{
		if (hasMergeQueueWorkflow is { } known)
			return known;
		try
		{
			string paths = await ExternalTool.RunAsync("gh",
				["api", "repos/{owner}/{repo}/actions/workflows",
					"--jq", ".workflows[] | select(.state == \"active\") | .path"], repoPath, ct);
			return (hasMergeQueueWorkflow = paths.Contains(MergeQueueWorkflow, StringComparison.Ordinal)).Value;
		}
		catch (ToolFailedException)
		{
			return (hasMergeQueueWorkflow = false).Value;
		}
	}

	/// <summary>
	/// Tells the drainer workflow there is something to do. A push to refs/stampeded/* triggers
	/// nothing - `on: push` accepts only branches and tags - so the queue cannot be its own
	/// event and this is sent instead. GitHub only runs the workflow on the default branch,
	/// which is where the drainer lives.
	/// </summary>
	public Task DispatchMergeQueueAsync(CancellationToken ct = default)
		=> ExternalTool.RunAsync("gh",
			["api", "repos/{owner}/{repo}/dispatches", "-f", $"event_type={MergeQueueEvent}"], repoPath, ct);

	/// <summary>Merges the pull request. <paramref name="method"/> is a gh flag name:
	/// merge, squash or rebase.</summary>
	public Task<string> MergePrAsync(int number, string method, CancellationToken ct = default)
		=> ExternalTool.RunAsync("gh", ["pr", "merge", number.ToString(), $"--{method}"], repoPath, ct);

	/// <summary>
	/// Takes a pull request out of draft. GitHub then requests the reviews the repository's rules
	/// ask for, which is the point of it, and `gh pr ready --undo` puts it back - so unlike a
	/// merge this is not a thing the reader has to be asked twice about.
	/// </summary>
	public Task<string> MarkReadyForReviewAsync(int number, CancellationToken ct = default)
		=> ExternalTool.RunAsync("gh", ["pr", "ready", number.ToString()], repoPath, ct);

	/// <summary>Log lines of the failed steps of a workflow run.</summary>
	/// <summary>The drainer workflow's path in a repository that has one, and the event that
	/// wakes it. Both are named by <c>.github/stampeded-merge-queue.yml</c> in this repository,
	/// which is the file to copy into a repository whose queue should empty itself.</summary>
	public const string MergeQueueWorkflow = "stampeded-merge-queue.yml";
	public const string MergeQueueEvent = "stampeded-merge-queue";

	bool? hasMergeQueueWorkflow;

	public Task<string> GetFailedLogAsync(long runId, CancellationToken ct = default)
		=> ExternalTool.RunAsync("gh", ["run", "view", runId.ToString(), "--log-failed"], repoPath, ct);

	/// <summary>Existing line comments of the PR's reviews. The {owner}/{repo} placeholders
	/// are resolved by gh from the repository's origin.</summary>
	public Task<IReadOnlyList<PostedComment>> GetReviewCommentsAsync(int number, CancellationToken ct = default)
		=> JsonAsync<IReadOnlyList<PostedComment>>(ct,
			"api", $"repos/{{owner}}/{{repo}}/pulls/{number}/comments", "--paginate");

	/// <summary>Every review submitted on the pull request, oldest first - several per person
	/// when they came back to it.</summary>
	public Task<IReadOnlyList<PrReview>> GetReviewsAsync(int number, CancellationToken ct = default)
		=> JsonAsync<IReadOnlyList<PrReview>>(ct,
			"api", $"repos/{{owner}}/{{repo}}/pulls/{number}/reviews", "--paginate");

	/// <summary>Review-thread resolution states via GraphQL (REST does not expose them).</summary>
	public async Task<IReadOnlyList<ThreadResolution>> GetThreadResolutionsAsync(int number, CancellationToken ct = default)
	{
		const string query = """
			query($owner: String!, $repo: String!, $number: Int!) {
			  repository(owner: $owner, name: $repo) {
			    pullRequest(number: $number) {
			      reviewThreads(first: 100) {
			        nodes { id isResolved comments(first: 50) { nodes { databaseId } } }
			      }
			    }
			  }
			}
			""";
		var (owner, repo) = await GetOwnerRepoAsync(ct);
		var result = await CliWrap.Cli.Wrap("gh")
			.WithArguments(["api", "graphql",
				"-f", $"query={query}", "-f", $"owner={owner}", "-f", $"repo={repo}", "-F", $"number={number}"])
			.WithWorkingDirectory(repoPath)
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		if (result.ExitCode != 0)
			throw new ToolFailedException("gh", result.ExitCode, result.StandardError);
		var resolutions = new List<ThreadResolution>();
		using var doc = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
		var nodes = doc.RootElement
			.GetProperty("data").GetProperty("repository").GetProperty("pullRequest")
			.GetProperty("reviewThreads").GetProperty("nodes");
		foreach (var node in nodes.EnumerateArray())
		{
			var ids = node.GetProperty("comments").GetProperty("nodes").EnumerateArray()
				.Select(c => c.GetProperty("databaseId").GetInt64())
				.ToList();
			resolutions.Add(new ThreadResolution(
				node.GetProperty("id").GetString() ?? "", node.GetProperty("isResolved").GetBoolean(), ids));
		}
		return resolutions;
	}

	/// <summary>Marks a review thread resolved or unresolved (GraphQL mutation).</summary>
	public async Task SetThreadResolvedAsync(string threadId, bool resolved, CancellationToken ct = default)
	{
		string mutation = resolved
			? "mutation($id: ID!) { resolveReviewThread(input: { threadId: $id }) { thread { id } } }"
			: "mutation($id: ID!) { unresolveReviewThread(input: { threadId: $id }) { thread { id } } }";
		var result = await CliWrap.Cli.Wrap("gh")
			.WithArguments(["api", "graphql", "-f", $"query={mutation}", "-f", $"id={threadId}"])
			.WithWorkingDirectory(repoPath)
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		Infra.CliLog.Write("gh", $"{(resolved ? "resolve" : "unresolve")} thread -> exit {result.ExitCode}");
		if (result.ExitCode != 0)
			throw new ToolFailedException("gh", result.ExitCode, result.StandardError + result.StandardOutput);
	}

	(string Owner, string Repo)? ownerRepo;

	/// <summary>Where an issue number of this repository points, or null when the repository
	/// is not on GitHub - a review of a local branch in a clone with no such remote.</summary>
	public async Task<string?> GetIssueUrlPrefixAsync(CancellationToken ct = default)
	{
		try
		{
			var (owner, repo) = await GetOwnerRepoAsync(ct);
			return $"https://github.com/{owner}/{repo}/issues/";
		}
		catch (ToolFailedException)
		{
			return null;
		}
	}

	async Task<(string Owner, string Repo)> GetOwnerRepoAsync(CancellationToken ct)
	{
		if (ownerRepo is { } cached)
			return cached;
		var result = await CliWrap.Cli.Wrap("gh")
			.WithArguments(["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"])
			.WithWorkingDirectory(repoPath)
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		if (result.ExitCode != 0)
			throw new ToolFailedException("gh", result.ExitCode, result.StandardError);
		var parts = result.StandardOutput.Trim().Split('/');
		if (parts.Length != 2)
			throw new ToolFailedException("gh", 1, $"unexpected nameWithOwner: {result.StandardOutput}");
		ownerRepo = (parts[0], parts[1]);
		return ownerRepo.Value;
	}

	/// <summary>Rebases the PR branch onto its target via GitHub's update-branch API
	/// (server-side; rewrites the PR branch, no local checkout involved).</summary>
	public async Task UpdateBranchAsync(int number, CancellationToken ct = default)
	{
		var result = await CliWrap.Cli.Wrap("gh")
			.WithArguments(["api", "-X", "PUT", $"repos/{{owner}}/{{repo}}/pulls/{number}/update-branch",
				"-f", "update_method=rebase"])
			.WithWorkingDirectory(repoPath)
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		CliLog.Write("gh", $"update-branch (rebase) #{number} -> exit {result.ExitCode}"
			+ (result.ExitCode != 0 ? ": " + ExternalTool.FailureReason(result.StandardError, result.StandardOutput) : ""));
		if (result.ExitCode != 0)
			throw new ToolFailedException("gh", result.ExitCode, result.StandardError + result.StandardOutput);
	}

	/// <summary>Submits a review (APPROVE / REQUEST_CHANGES / COMMENT) with line comments.</summary>
	public async Task SubmitReviewAsync(int number, ReviewSubmission submission, CancellationToken ct = default)
	{
		string json = JsonSerializer.Serialize(Attributed(submission), JsonOptions);
		var result = await CliWrap.Cli.Wrap("gh")
			.WithArguments(["api", "-X", "POST", $"repos/{{owner}}/{{repo}}/pulls/{number}/reviews", "--input", "-"])
			.WithWorkingDirectory(repoPath)
			.WithStandardInputPipe(CliWrap.PipeSource.FromString(json))
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		CliLog.Write("gh", $"submit review ({submission.Event}, {submission.Comments.Count} comment(s)) -> exit {result.ExitCode}"
			+ (result.ExitCode != 0 ? ": " + ExternalTool.FailureReason(result.StandardError, result.StandardOutput) : ""));
		if (result.ExitCode != 0)
			throw new ToolFailedException("gh", result.ExitCode, result.StandardError + result.StandardOutput);
	}

	/// <summary>
	/// Answers an existing review comment inside its thread. A review submission cannot carry
	/// this: its comments only take a path and a line, which starts a new thread on that line
	/// however much it was meant as an answer. So a reply is its own request, and it is posted
	/// on its own rather than as part of a pending review.
	/// </summary>
	public async Task ReplyToCommentAsync(int number, long commentId, string body, CancellationToken ct = default)
	{
		string json = JsonSerializer.Serialize(new ReplyBody(body), JsonOptions);
		var result = await CliWrap.Cli.Wrap("gh")
			.WithArguments(["api", "-X", "POST",
				$"repos/{{owner}}/{{repo}}/pulls/{number}/comments/{commentId}/replies", "--input", "-"])
			.WithWorkingDirectory(repoPath)
			.WithStandardInputPipe(CliWrap.PipeSource.FromString(json))
			.WithValidation(CliWrap.CommandResultValidation.None)
			.ExecuteBufferedAsync(ct);
		Infra.CliLog.Write("gh", $"reply to comment {commentId} -> exit {result.ExitCode}"
			+ (result.ExitCode != 0 ? ": " + ExternalTool.FailureReason(result.StandardError, result.StandardOutput) : ""));
		if (result.ExitCode != 0)
			throw new ToolFailedException("gh", result.ExitCode, result.StandardError + result.StandardOutput);
	}

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
