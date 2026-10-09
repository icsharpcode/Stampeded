using System.Text.Json;

using Stampeded.Core.Infra;
using Stampeded.Core.PullRequests;

namespace Stampeded.Core.Bitbucket;

/// <summary>
/// Bitbucket Data Center access through curl and .netrc. There is no first-party CLI in the
/// shape gh and az provide, so curl is the external tool that owns HTTP and authentication.
/// </summary>
public sealed class BitbucketService(string repoPath, string baseUrl, string projectKey, string repo)
	: IPullRequestHost
{
	readonly string baseUrl = baseUrl.TrimEnd('/');
	readonly Dictionary<int, Task<JsonDocument>> pullRequests = [];
	readonly Dictionary<int, string> headRefspecs = [];
	string? viewerLogin;
	string? defaultBranch;

	public string Name => "Bitbucket Data Center";

	/// <summary>Bitbucket Data Center lets an author approve their own pull request unless rules refuse it.</summary>
	public bool AcceptsOwnApproval => true;

	string ApiBase => $"{this.baseUrl}/rest/api/latest/projects/{Esc(projectKey)}/repos/{Esc(repo)}";

	string GitApiBase => $"{this.baseUrl}/rest/git/latest/projects/{Esc(projectKey)}/repos/{Esc(repo)}";

	string WebBase => $"{this.baseUrl}/projects/{Esc(projectKey)}/repos/{Esc(repo)}";

	static string Esc(string value) => Uri.EscapeDataString(value);

	static string? Str(JsonElement element, params string[] path)
	{
		foreach (string name in path)
		{
			if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
				return null;
		}
		return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
	}

	static JsonElement? Node(JsonElement element, params string[] path)
	{
		foreach (string name in path)
		{
			if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
				return null;
		}
		return element;
	}

	static IEnumerable<JsonElement> Array(JsonElement? element)
		=> element is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

	static bool Bool(JsonElement element, params string[] path)
		=> Node(element, path)?.ValueKind == JsonValueKind.True;

	static int Int(JsonElement element, params string[] path)
		=> Node(element, path) is { ValueKind: JsonValueKind.Number } node && node.TryGetInt32(out int value)
			? value
			: 0;

	static long Long(JsonElement element, params string[] path)
		=> Node(element, path) is { ValueKind: JsonValueKind.Number } node && node.TryGetInt64(out long value)
			? value
			: 0;

	static string StripRefsHeads(string? refName)
		=> refName is { Length: > 0 } name && name.StartsWith("refs/heads/", StringComparison.Ordinal)
			? name["refs/heads/".Length..]
			: refName ?? "";

	static string RefName(JsonElement element, string name)
		=> StripRefsHeads(Str(element, name, "id") ?? Str(element, name, "displayId") ?? Str(element, name));

	async Task<JsonDocument> JsonAsync(string method, string url, string? jsonBody, CancellationToken ct)
	{
		string output = await CurlAsync(method, url, jsonBody, ct);
		return JsonDocument.Parse(output.Trim().Length == 0 ? "{}" : output);
	}

	async Task<string> CurlAsync(string method, string url, string? jsonBody, CancellationToken ct)
	{
		string? file = null;
		var watch = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			var args = new List<string> {
				"--silent", "--show-error", "--fail-with-body",
				"--request", method,
				"--header", "Accept: application/json;charset=UTF-8",
			};
			if (Environment.GetEnvironmentVariable("STAMPEDED_NETRC") is { Length: > 0 } netrc)
				args.AddRange(["--netrc-file", netrc]);
			else
				args.Add("--netrc");
			if (jsonBody is not null)
			{
				file = Path.Combine(Path.GetTempPath(), $"stampeded-bitbucket-{Guid.NewGuid():N}.json");
				await File.WriteAllTextAsync(file, jsonBody, ct);
				args.AddRange(["--header", "Content-Type: application/json", "--data", $"@{file}"]);
			}
			args.Add(url);
			CliLog.Write("bitbucket", $"{method} {RouteForLog(url)}");
			try
			{
				string output = await ExternalTool.RunAsync("curl", args, repoPath, ct, logCommand: false);
				CliLog.Write("bitbucket", $"{method} {RouteForLog(url)} -> exit 0 ({watch.ElapsedMilliseconds} ms)");
				return output;
			}
			catch (ToolFailedException ex)
			{
				CliLog.Write("bitbucket", $"{method} {RouteForLog(url)} -> exit {ex.ExitCode} ({watch.ElapsedMilliseconds} ms): "
					+ ExternalTool.FailureReason(ex.StdErr, ex.StdOut));
				LogCurlFailure(ex);
				throw;
			}
		}
		finally
		{
			if (file is not null && File.Exists(file))
				File.Delete(file);
		}
	}

	static void LogCurlFailure(ToolFailedException ex)
	{
		string[] streams = ex.StdOut.Trim().Length == 0 ? [ex.StdErr] : [ex.StdOut];
		foreach (string line in ErrorLines(streams))
			CliLog.Write("curl", line);
	}

	static IEnumerable<string> ErrorLines(params string[] streams)
	{
		foreach (string stream in streams)
		{
			foreach (string line in stream.ReplaceLineEndings("\n").Split('\n'))
			{
				string text = line.TrimEnd();
				if (text.Length > 0)
					yield return text;
			}
		}
	}

	static string RouteForLog(string url)
		=> Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.PathAndQuery : url;

	async Task<IReadOnlyList<JsonElement>> PageAsync(string url, CancellationToken ct)
	{
		var values = new List<JsonElement>();
		int start = 0;
		while (true)
		{
			string sep = url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
			using var page = await JsonAsync("GET", $"{url}{sep}start={start}&limit=100", null, ct);
			values.AddRange(Array(Node(page.RootElement, "values")).Select(v => v.Clone()));
			if (Bool(page.RootElement, "isLastPage"))
				return values;
			int next = Int(page.RootElement, "nextPageStart");
			if (next <= start)
				return values;
			start = next;
		}
	}

	Task<JsonDocument> PrAsync(int number, CancellationToken ct)
	{
		if (!pullRequests.TryGetValue(number, out var pending))
			pullRequests[number] = pending = JsonAsync("GET", $"{ApiBase}/pull-requests/{number}", null, ct);
		return pending;
	}

	void Forget(int number) => pullRequests.Remove(number);

	async Task<JsonDocument> FreshPrAsync(int number, CancellationToken ct)
	{
		Forget(number);
		return await PrAsync(number, ct);
	}

	public Task<string?> PrUrlAsync(int number, CancellationToken ct = default)
		=> Task.FromResult<string?>($"{WebBase}/pull-requests/{number}");

	public Task<string?> CommitUrlAsync(string sha, CancellationToken ct = default)
		=> Task.FromResult<string?>($"{WebBase}/commits/{sha}");

	public Task<string> GetViewerLoginAsync(CancellationToken ct = default)
		=> Task.FromResult(viewerLogin ??= Environment.GetEnvironmentVariable("STAMPEDED_BITBUCKET_USER")
			?? LoginFromNetrc()
			?? throw new RefusedException("Bitbucket Data Center user is not known. Set STAMPEDED_BITBUCKET_USER or put a login for this host in .netrc."));

	string? LoginFromNetrc()
	{
		string? path = Environment.GetEnvironmentVariable("STAMPEDED_NETRC");
		if (path is not { Length: > 0 })
			path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".netrc");
		if (!File.Exists(path) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
			return null;
		string[] tokens = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i + 1 < tokens.Length; i++)
		{
			if (tokens[i] != "machine" || !string.Equals(tokens[i + 1], uri.Host, StringComparison.OrdinalIgnoreCase))
				continue;
			for (int j = i + 2; j + 1 < tokens.Length && tokens[j] != "machine"; j++)
			{
				if (tokens[j] == "login")
					return tokens[j + 1];
			}
		}
		return null;
	}

	public async Task<string> GetDefaultBranchAsync(CancellationToken ct = default)
	{
		if (defaultBranch is { Length: > 0 })
			return defaultBranch;
		using var doc = await JsonAsync("GET", ApiBase, null, ct);
		string? branchText = Str(doc.RootElement, "defaultBranch");
		string? branchId = Str(doc.RootElement, "defaultBranch", "id");
		string? branchDisplay = Str(doc.RootElement, "defaultBranch", "displayId");
		CliLog.Write("bitbucket", "repository defaultBranch: "
			+ $"string={(branchText is null ? "missing" : "present")}, "
			+ $"id={(branchId is null ? "missing" : "present")}, "
			+ $"displayId={(branchDisplay is null ? "missing" : "present")}");
		defaultBranch = StripRefsHeads(branchText ?? branchId ?? branchDisplay);
		if (defaultBranch.Length == 0)
			throw new RefusedException("Bitbucket did not name the repository's default branch.");
		return defaultBranch;
	}

	public async Task<IReadOnlyList<PrSummary>> ListOpenPrsAsync(CancellationToken ct = default)
	{
		var prs = await PageAsync($"{ApiBase}/pull-requests?state=OPEN", ct);
		string viewer = await GetViewerLoginAsync(ct);
		foreach (var pr in prs)
		{
			int id = Int(pr, "id");
			if (id > 0 && TryHeadRefspec(pr, id) is { } refspec)
				headRefspecs[id] = refspec;
		}
		return [.. prs.Select(pr => Summary(pr) with { ViewerLogin = viewer, OriginOwner = projectKey })];
	}

	public static PrDiffStats DiffStats(JsonElement root)
	{
		int additions = 0, deletions = 0, changedFiles = 0;
		foreach (var diff in Array(Node(root, "diffs")))
		{
			changedFiles++;
			foreach (var hunk in Array(Node(diff, "hunks")))
			{
				foreach (var segment in Array(Node(hunk, "segments")))
				{
					int count = Node(segment, "lines") is { ValueKind: JsonValueKind.Array } lines
						? lines.GetArrayLength()
						: 0;
					switch (Str(segment, "type"))
					{
						case "ADDED":
							additions += count;
							break;
						case "REMOVED":
							deletions += count;
							break;
					}
				}
			}
		}
		return new PrDiffStats(additions, deletions, changedFiles);
	}

	PrSummary Summary(JsonElement pr)
	{
		var reviewers = Array(Node(pr, "reviewers")).ToList();
		var latest = reviewers
			.Where(r => ReviewState(r) is { } state)
			.Select(r => new PrLatestReview(new PrAuthor(User(r)), ReviewState(r)))
			.ToList();
		var requested = reviewers
			.Where(r => ReviewState(r) is null)
			.Select(r => new PrReviewRequest(User(r)))
			.ToList();
		return new PrSummary(
			Int(pr, "id"),
			Str(pr, "title") ?? "",
			new PrAuthor(User(Node(pr, "author") ?? default)),
			RefName(pr, "fromRef"),
			RefName(pr, "toRef"),
			Bool(pr, "draft"),
			FromBitbucketTime(Long(pr, "updatedDate")),
			StatusCheckRollup: null,
			HeadRefOid: Str(pr, "fromRef", "latestCommit"),
			ReviewDecision: Decision(reviewers),
			LatestReviews: latest,
			ReviewRequests: requested,
			HeadRepositoryOwner: new PrRepoOwner(Str(pr, "fromRef", "repository", "project", "key") ?? projectKey));
	}

	public async Task<PrDetail> GetPrAsync(int number, CancellationToken ct = default)
	{
		var doc = await FreshPrAsync(number, ct);
		var pr = doc.RootElement;
		return new PrDetail(
			number,
			Str(pr, "title") ?? "",
			Str(pr, "description"),
			RefName(pr, "toRef"),
			RefName(pr, "fromRef"),
			Str(pr, "state") ?? "",
			new PrAuthor(User(Node(pr, "author") ?? default)),
			Bool(pr, "draft"));
	}

	static DateTimeOffset FromBitbucketTime(long milliseconds)
		=> milliseconds > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : default;

	static string User(JsonElement? participant)
		=> participant is { ValueKind: JsonValueKind.Object } p
			? Str(p, "user", "slug") ?? Str(p, "user", "name") ?? Str(p, "user", "displayName") ?? ""
			: "";

	static string? ReviewState(JsonElement reviewer)
	{
		if (Bool(reviewer, "approved") || Str(reviewer, "status") == "APPROVED")
			return "APPROVED";
		return Str(reviewer, "status") is "NEEDS_WORK" ? "CHANGES_REQUESTED" : null;
	}

	static string? Decision(IReadOnlyList<JsonElement> reviewers)
	{
		if (reviewers.Any(r => ReviewState(r) == "CHANGES_REQUESTED"))
			return "CHANGES_REQUESTED";
		return reviewers.Count > 0 && reviewers.All(r => ReviewState(r) == "APPROVED") ? "APPROVED" : null;
	}

	public async Task<string> PrHeadRefspecAsync(int number, CancellationToken ct = default)
	{
		if (headRefspecs.TryGetValue(number, out string? refspec))
			return refspec;
		var doc = await PrAsync(number, ct);
		if (TryHeadRefspec(doc.RootElement, number) is { } computed)
			return computed;
		throw new RefusedException($"Bitbucket did not name a source branch for pull request {number}.");
	}

	string? TryHeadRefspec(JsonElement pr, int number)
	{
		string fromProject = Str(pr, "fromRef", "repository", "project", "key") ?? projectKey;
		string fromRepo = Str(pr, "fromRef", "repository", "slug") ?? repo;
		if (!string.Equals(fromProject, projectKey, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(fromRepo, repo, StringComparison.OrdinalIgnoreCase))
			return null;
		string branch = RefName(pr, "fromRef");
		if (branch.Length == 0)
			return null;
		return $"+refs/heads/{branch}:refs/stampeded/pr/{number}";
	}

	public Task<IReadOnlyList<CheckRun>> GetChecksAsync(int number, CancellationToken ct = default)
		=> Task.FromResult<IReadOnlyList<CheckRun>>([]);

	public Task<string> GetFailedLogAsync(long runId, CancellationToken ct = default)
		=> throw new RefusedException("Jenkins logs are not available through Bitbucket Data Center.");

	public async Task<MergeState> GetMergeStateAsync(int number, CancellationToken ct = default)
	{
		var prDoc = await FreshPrAsync(number, ct);
		using var mergeDoc = await JsonAsync("GET", $"{ApiBase}/pull-requests/{number}/merge", null, ct);
		var pr = prDoc.RootElement;
		var merge = mergeDoc.RootElement;
		bool draft = Bool(pr, "draft");
		bool canMerge = Bool(merge, "canMerge");
		bool conflicted = Bool(merge, "conflicted");
		using var rollup = JsonDocument.Parse("[]");
		return new MergeState(
			canMerge ? "MERGEABLE" : conflicted ? "CONFLICTING" : "UNKNOWN",
			draft ? "DRAFT" : canMerge ? "CLEAN" : "BLOCKED",
			Decision([.. Array(Node(pr, "reviewers"))]),
			draft,
			RefName(pr, "toRef"),
			rollup.RootElement.Clone(),
			Str(pr, "state"),
			Str(pr, "fromRef", "latestCommit")) {
			Host = Name,
		};
	}

	public Task<string?> GetIssueTitleAsync(int number, CancellationToken ct = default)
		=> Task.FromResult<string?>(null);

	public Task<string?> GetIssueUrlPrefixAsync(CancellationToken ct = default)
		=> Task.FromResult<string?>(null);

	public Task<MergeMethods> GetMergeMethodsAsync(CancellationToken ct = default)
		=> Task.FromResult(new MergeMethods(MergeCommitAllowed: true, SquashMergeAllowed: true, RebaseMergeAllowed: false));

	public async Task<string> MergePrAsync(int number, string method, bool deleteBranch = false,
		CancellationToken ct = default)
	{
		if (method == "rebase")
			throw new RefusedException("Bitbucket Data Center merges cannot use Stampeded's rebase merge method.");
		var pr = await FreshPrAsync(number, ct);
		string strategy = method == "squash" ? "squash" : "no-ff";
		string url = $"{ApiBase}/pull-requests/{number}/merge?version={Int(pr.RootElement, "version")}&strategyId={strategy}";
		using var _ = await JsonAsync("POST", url, "{}", ct);
		Forget(number);
		return $"merged with {method}";
	}

	public async Task<string> MarkReadyForReviewAsync(int number, CancellationToken ct = default)
	{
		var pr = await FreshPrAsync(number, ct);
		string body = JsonSerializer.Serialize(new {
			version = Int(pr.RootElement, "version"),
			title = Str(pr.RootElement, "title"),
			description = Str(pr.RootElement, "description"),
			draft = false,
		});
		using var _ = await JsonAsync("PUT", $"{ApiBase}/pull-requests/{number}", body, ct);
		Forget(number);
		return $"pull request {number} is ready for review";
	}

	public async Task UpdateBranchAsync(int number, CancellationToken ct = default)
	{
		var pr = await FreshPrAsync(number, ct);
		string body = JsonSerializer.Serialize(new { version = Int(pr.RootElement, "version") });
		using var _ = await JsonAsync("POST", $"{GitApiBase}/pull-requests/{number}/rebase", body, ct);
		Forget(number);
	}

	public async Task<IReadOnlyList<PostedComment>> GetReviewCommentsAsync(int number, CancellationToken ct = default)
	{
		var comments = await PrCommentsAsync(number, ct);
		var posted = new List<PostedComment>();
		foreach (var comment in comments)
			AddComment(posted, comment.Comment, comment.Anchor);
		CliLog.Write("bitbucket", $"read {comments.Count} pull request comment root(s), {posted.Count} anchored comment(s)");
		return posted;
	}

	async Task<IReadOnlyList<BitbucketComment>> PrCommentsAsync(int number, CancellationToken ct)
	{
		var byId = new Dictionary<long, BitbucketComment>();
		foreach (var activity in await PageAsync($"{ApiBase}/pull-requests/{number}/activities", ct))
			if (Node(activity, "comment") is { ValueKind: JsonValueKind.Object } comment)
				Add(comment, Node(activity, "commentAnchor") ?? Node(activity, "anchor"));
		return [.. byId.Values];

		void Add(JsonElement comment, JsonElement? anchor)
		{
			long id = Long(comment, "id");
			if (id != 0)
				byId[id] = new BitbucketComment(comment.Clone(), anchor?.Clone());
		}
	}

	sealed record BitbucketComment(JsonElement Comment, JsonElement? Anchor);

	void AddComment(List<PostedComment> posted, JsonElement comment, JsonElement? inheritedAnchor)
	{
		var anchor = Node(comment, "anchor") ?? inheritedAnchor;
		if (anchor is null)
			return;
		string? path = anchor is { } a ? Str(a, "path") : null;
		int? line = anchor is { } b && Node(b, "line") is { ValueKind: JsonValueKind.Number } lineNode
			? lineNode.GetInt32()
			: null;
		string? lineType = anchor is { } c ? Str(c, "lineType") : null;
		posted.Add(new PostedComment(
			Long(comment, "id"),
			Str(comment, "text") ?? "",
			path ?? "",
			line,
			lineType == "REMOVED" ? "LEFT" : "RIGHT",
			new PostedUser(Str(comment, "author", "slug") ?? Str(comment, "author", "name") ?? Str(comment, "author", "displayName") ?? ""),
			OriginalLine: line,
			DiffHunk: null,
			OriginalCommitId: anchor is { } d ? Str(d, "toHash") ?? Str(d, "fromHash") : null,
			HtmlUrl: FirstLink(comment, "self")));
		foreach (var child in Array(Node(comment, "comments")))
			AddComment(posted, child, anchor);
	}

	static string? FirstLink(JsonElement element, string rel)
	{
		if (Node(element, "links", rel) is not { ValueKind: JsonValueKind.Array } links)
			return null;
		foreach (var link in links.EnumerateArray())
		{
			if (Str(link, "href") is { Length: > 0 } href)
				return href;
		}
		return null;
	}

	public async Task<IReadOnlyList<PrReview>> GetReviewsAsync(int number, CancellationToken ct = default)
	{
		var doc = await FreshPrAsync(number, ct);
		return [.. Array(Node(doc.RootElement, "reviewers"))
			.Where(r => ReviewState(r) is not null)
			.Select(r => new PrReview(
				new PostedUser(User(r)),
				ReviewState(r),
				Str(r, "lastReviewedCommit"),
				SubmittedAt: null))];
	}

	public Task<IReadOnlyList<ThreadResolution>> GetThreadResolutionsAsync(int number, CancellationToken ct = default)
		=> ReadThreadResolutionsAsync(number, ct);

	async Task<IReadOnlyList<ThreadResolution>> ReadThreadResolutionsAsync(int number, CancellationToken ct)
	{
		var comments = await PrCommentsAsync(number, ct);
		var resolutions = new List<ThreadResolution>();
		foreach (var entry in comments)
		{
			var comment = entry.Comment;
			long id = Long(comment, "id");
			if (id == 0)
				continue;
			var ids = new List<long> { id };
			CollectChildIds(comment, ids);
			resolutions.Add(new ThreadResolution(
				$"{number}/{id}", Str(comment, "state") == "RESOLVED", ids));
		}
		return resolutions;
	}

	static void CollectChildIds(JsonElement comment, List<long> ids)
	{
		foreach (var child in Array(Node(comment, "comments")))
		{
			long id = Long(child, "id");
			if (id != 0)
				ids.Add(id);
			CollectChildIds(child, ids);
		}
	}

	public async Task SetThreadResolvedAsync(string threadId, bool resolved, CancellationToken ct = default)
	{
		string[] parts = threadId.Split('/');
		if (parts.Length != 2 || !int.TryParse(parts[0], out int number) || !long.TryParse(parts[1], out long commentId))
			throw new RefusedException($"Not a Bitbucket Data Center thread id: {threadId}");

		using var comment = await JsonAsync("GET", $"{ApiBase}/pull-requests/{number}/comments/{commentId}", null, ct);
		string body = JsonSerializer.Serialize(new {
			version = Int(comment.RootElement, "version"),
			text = Str(comment.RootElement, "text") ?? "",
			severity = Str(comment.RootElement, "severity") ?? "NORMAL",
			state = resolved ? "RESOLVED" : "OPEN",
		});
		using var _ = await JsonAsync("PUT", $"{ApiBase}/pull-requests/{number}/comments/{commentId}", body, ct);
	}

	public async Task SubmitReviewAsync(int number, ReviewSubmission submission, CancellationToken ct = default)
	{
		var attributed = ReviewAttribution.Attributed(submission);
		int posted = 0;
		try
		{
			foreach (var comment in attributed.Comments)
			{
				await PostCommentAsync(number, comment.Body, comment.Path, comment.Line, comment.Side, parent: null, ct);
				posted++;
			}
			if (attributed.Body.Trim().Length > 0)
			{
				await PostCommentAsync(number, attributed.Body, path: null, line: null, side: null, parent: null, ct);
				posted++;
			}
		}
		catch (Exception)
		{
			if (posted > 0)
				CliLog.Write("bitbucket", $"{posted} comment(s) were posted before the failure; no review status was changed");
			throw;
		}

		string? status = attributed.Event switch {
			"APPROVE" => "APPROVED",
			"REQUEST_CHANGES" => "NEEDS_WORK",
			_ => null,
		};
		if (status is null)
			return;
		string user = await GetViewerLoginAsync(ct);
		string body = JsonSerializer.Serialize(new { status });
		using var _ = await JsonAsync("PUT", $"{ApiBase}/pull-requests/{number}/participants/{Esc(user)}", body, ct);
		Forget(number);
	}

	async Task PostCommentAsync(int number, string text, string? path, int? line, string? side,
		long? parent, CancellationToken ct)
	{
		object body = path is null
			? parent is { } parentId
				? new { text, parent = new { id = parentId } }
				: new { text }
			: new {
				text,
				anchor = new {
					path,
					line = line ?? 1,
					lineType = side == "LEFT" ? "REMOVED" : "ADDED",
				},
			};
		using var _ = await JsonAsync("POST", $"{ApiBase}/pull-requests/{number}/comments",
			JsonSerializer.Serialize(body), ct);
	}

	public Task ReplyToCommentAsync(int number, long commentId, string body, CancellationToken ct = default)
		=> PostCommentAsync(number, body, path: null, line: null, side: null, parent: commentId, ct);

	public Task<bool> HasMergeQueueWorkflowAsync(CancellationToken ct = default) => Task.FromResult(false);

	public Task DispatchMergeQueueAsync(CancellationToken ct = default) => Task.CompletedTask;
}
