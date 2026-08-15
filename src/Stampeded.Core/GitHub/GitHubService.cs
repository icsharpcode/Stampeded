using CliWrap.Buffered;

using System.Text.Json;
using System.Text.Json.Serialization;

using Stampeded.Core.Infra;

namespace Stampeded.Core.GitHub;

public sealed record PrAuthor(string Login);

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
	string? ReviewDecision = null)
{
	/// <summary>"fail" / "pending" / "green" / "none", folded from the check rollup
	/// (check runs carry status+conclusion, legacy status contexts carry state).</summary>
	public string ChecksBucket {
		get {
			if (StatusCheckRollup is not { ValueKind: System.Text.Json.JsonValueKind.Array } rollup
				|| rollup.GetArrayLength() == 0)
				return "none";
			bool pending = false;
			foreach (var item in rollup.EnumerateArray())
			{
				string? conclusion = item.TryGetProperty("conclusion", out var c) ? c.GetString() : null;
				string? state = item.TryGetProperty("state", out var s) ? s.GetString() : null;
				string verdict = (conclusion is { Length: > 0 } ? conclusion : state) ?? "";
				switch (verdict.ToUpperInvariant())
				{
					case "FAILURE" or "ERROR" or "TIMED_OUT" or "STARTUP_FAILURE" or "CANCELLED" or "ACTION_REQUIRED":
						return "fail";
					case "" or "PENDING" or "IN_PROGRESS" or "QUEUED" or "EXPECTED" or "WAITING" or "REQUESTED":
						pending = true;
						break;
				}
			}
			return pending ? "pending" : "green";
		}
	}

	public bool ChecksFailed => ChecksBucket == "fail";
	public bool ChecksPending => ChecksBucket == "pending";
	public bool ChecksGreen => ChecksBucket == "green";

	public bool IsApproved => ReviewDecision == "APPROVED";
	public bool ChangesRequested => ReviewDecision == "CHANGES_REQUESTED";

	public string NumberDisplay => $"#{Number}";
}

public sealed record PrDetail(
	int Number,
	string Title,
	string? Body,
	string BaseRefName,
	string HeadRefName,
	string State,
	PrAuthor? Author);

public sealed record CheckRun(string Name, string State, string Bucket, string? Link, string? Workflow);

/// <summary>
/// Whether GitHub would take a merge of this pull request right now, in its own words:
/// <see cref="Mergeable"/> is MERGEABLE / CONFLICTING / UNKNOWN, <see cref="MergeStateStatus"/>
/// is CLEAN, UNSTABLE, BLOCKED, BEHIND, DIRTY, DRAFT, HAS_HOOKS or UNKNOWN.
/// </summary>
public sealed record MergeState(string? Mergeable, string? MergeStateStatus)
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
	[property: JsonPropertyName("html_url")] string? HtmlUrl = null);

/// <summary>Resolution state of one GitHub review thread and the REST ids of its comments.</summary>
public sealed record ThreadResolution(string ThreadId, bool IsResolved, IReadOnlyList<long> CommentIds);

public sealed record ReviewCommentDto(string Path, int Line, string Side, string Body);

public sealed record ReviewSubmission(string Body, string Event, IReadOnlyList<ReviewCommentDto> Comments);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(IReadOnlyList<PrSummary>))]
[JsonSerializable(typeof(PrDetail))]
[JsonSerializable(typeof(IReadOnlyList<CheckRun>))]
[JsonSerializable(typeof(MergeState))]
[JsonSerializable(typeof(MergeMethods))]
[JsonSerializable(typeof(IReadOnlyList<PostedComment>))]
[JsonSerializable(typeof(ReviewSubmission))]
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
			"--json", "number,title,author,headRefName,baseRefName,isDraft,updatedAt,statusCheckRollup,headRefOid,reviewDecision",
			"--limit", "50");

	public Task<PrDetail> GetPrAsync(int number, CancellationToken ct = default)
		=> JsonAsync<PrDetail>(ct,
			"pr", "view", number.ToString(),
			"--json", "number,title,body,baseRefName,headRefName,state,author");

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
		=> JsonAsync<MergeState>(ct, "pr", "view", number.ToString(), "--json", "mergeable,mergeStateStatus");

	/// <summary>The merge methods the repository allows; a setting, so it is read once.</summary>
	public async Task<MergeMethods> GetMergeMethodsAsync(CancellationToken ct = default)
		=> mergeMethods ??= await JsonAsync<MergeMethods>(ct,
			"repo", "view", "--json", "mergeCommitAllowed,squashMergeAllowed,rebaseMergeAllowed");

	/// <summary>Merges the pull request. <paramref name="method"/> is a gh flag name:
	/// merge, squash or rebase.</summary>
	public Task<string> MergePrAsync(int number, string method, CancellationToken ct = default)
		=> ExternalTool.RunAsync("gh", ["pr", "merge", number.ToString(), $"--{method}"], repoPath, ct);

	/// <summary>Log lines of the failed steps of a workflow run.</summary>
	public Task<string> GetFailedLogAsync(long runId, CancellationToken ct = default)
		=> ExternalTool.RunAsync("gh", ["run", "view", runId.ToString(), "--log-failed"], repoPath, ct);

	/// <summary>Existing line comments of the PR's reviews. The {owner}/{repo} placeholders
	/// are resolved by gh from the repository's origin.</summary>
	public Task<IReadOnlyList<PostedComment>> GetReviewCommentsAsync(int number, CancellationToken ct = default)
		=> JsonAsync<IReadOnlyList<PostedComment>>(ct,
			"api", $"repos/{{owner}}/{{repo}}/pulls/{number}/comments", "--paginate");

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

	static string WithAttribution(string body)
		=> (body.Length > 0 ? body.TrimEnd() + "\n\n" : "")
			+ "*Reviewed with [Stampeded!](https://github.com/icsharpcode/Stampeded)*";
}
