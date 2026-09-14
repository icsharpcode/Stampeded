using CliWrap.Buffered;

using System.Text.Json;
using System.Text.Json.Serialization;

using Stampeded.Core.Infra;
using Stampeded.Core.PullRequests;

namespace Stampeded.Core.GitHub;

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
public sealed partial class GitHubService(string repoPath) : IPullRequestHost
{
	public string Name => "GitHub";

	/// <summary>GitHub refuses an approval from the pull request's own author outright.</summary>
	public bool AcceptsOwnApproval => false;

	/// <summary>Every pull request's head is a ref of the repository itself, fork or not.</summary>
	public Task<string> PrHeadRefspecAsync(int number, CancellationToken ct = default)
		=> Task.FromResult($"+refs/pull/{number}/head:refs/stampeded/pr/{number}");

	public Task<string?> PrUrlAsync(int number, CancellationToken ct = default)
		=> WebUrlAsync($"pull/{number}", ct);

	public Task<string?> CommitUrlAsync(string sha, CancellationToken ct = default)
		=> WebUrlAsync($"commit/{sha}", ct);

	async Task<string?> WebUrlAsync(string suffix, CancellationToken ct)
	{
		try
		{
			var (owner, repo) = await GetOwnerRepoAsync(ct);
			return $"https://github.com/{owner}/{repo}/{suffix}";
		}
		catch (ToolFailedException)
		{
			return null;
		}
	}

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
			"--json", "number,title,author,headRefName,baseRefName,isDraft,updatedAt,statusCheckRollup,headRefOid,reviewDecision,additions,deletions,changedFiles,latestReviews,reviewRequests,headRepositoryOwner",
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
		var checks = JsonSerializer.Deserialize<IReadOnlyList<CheckRun>>(result.StandardOutput, JsonOptions) ?? [];
		return [.. checks.Select(c => c with { RunId = RunIdOf(c.Link) })];
	}

	/// <summary>The run a check's link points at, which is what its failed log is fetched by.
	/// Only a GitHub Actions link carries one; a check reported by anything else links
	/// somewhere whose logs gh cannot fetch, and opens nothing.</summary>
	public static long? RunIdOf(string? link)
		=> link is not null && ActionsRunUrl().Match(link) is { Success: true } m
			? long.Parse(m.Groups[1].Value)
			: null;

	[System.Text.RegularExpressions.GeneratedRegex(@"/actions/runs/(\d+)")]
	private static partial System.Text.RegularExpressions.Regex ActionsRunUrl();

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
	/// merge, squash or rebase. <paramref name="deleteBranch"/> adds gh's own tidying, which
	/// takes the head branch off the remote and out of this clone as well - a branch some
	/// checkout still has stays, and gh says so.</summary>
	public Task<string> MergePrAsync(int number, string method, bool deleteBranch = false,
		CancellationToken ct = default)
		=> ExternalTool.RunAsync("gh",
			["pr", "merge", number.ToString(), $"--{method}", .. deleteBranch ? new[] { "--delete-branch" } : []],
			repoPath, ct);

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
	public Task<string?> GetIssueUrlPrefixAsync(CancellationToken ct = default)
		=> WebUrlAsync("issues/", ct);

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
		string json = JsonSerializer.Serialize(ReviewAttribution.Attributed(submission), JsonOptions);
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
}
