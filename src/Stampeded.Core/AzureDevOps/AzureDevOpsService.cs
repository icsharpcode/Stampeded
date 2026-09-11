using System.Text.Json;

using Stampeded.Core.Infra;
using Stampeded.Core.PullRequests;

namespace Stampeded.Core.AzureDevOps;

/// <summary>
/// Azure DevOps access through the `az` CLI and its azure-devops extension, run in the
/// repository directory. Auth rides on the user's `az login`, the way GitHub's rides on gh.
///
/// Every command names the organization and project explicitly rather than leaning on
/// `az devops configure --defaults`, which is a per-machine setting a reader may have pointed
/// at another project entirely.
///
/// What the extension has no verb for goes through <c>az devops invoke</c>, which is the
/// analogue of `gh api`: threads, iterations, build logs and the completion PATCH.
/// </summary>
public sealed class AzureDevOpsService(string repoPath, string org, string project, string repo)
	: IPullRequestHost
{
	/// <summary>The REST version the routes below are written against. Named on every invoke:
	/// az otherwise picks the newest the server offers, which is a moving target.</summary>
	const string ApiVersion = "7.1";

	readonly string orgUrl = $"https://dev.azure.com/{org}";
	readonly Dictionary<int, Task<JsonDocument>> pullRequests = [];
	readonly Dictionary<int, Task<string?>> workItemTitles = [];
	string? viewerLogin;
	MergeMethods? mergeMethods;

	public string Name => "Azure DevOps";

	/// <summary>Azure DevOps lets an author vote on their own pull request.</summary>
	public bool AcceptsOwnApproval => true;

	// ---- plumbing ----------------------------------------------------------------------

	/// <summary>Named on every command rather than left to `az devops configure --defaults`,
	/// which is a per-machine setting a reader may have pointed at another project entirely.
	/// The project is not among them: the commands addressed by pull-request id - show, policy
	/// list, set-vote, update - and `az devops invoke` take no --project and reject it.</summary>
	string[] OrgArgs => ["--organization", orgUrl, "--output", "json"];

	string[] ProjectArgs => [.. OrgArgs, "--project", project];

	/// <summary>One `az` command whose answer is JSON. <paramref name="inProject"/> for the
	/// commands that name the project rather than an id.</summary>
	async Task<JsonDocument> JsonAsync(CancellationToken ct, bool inProject, params string[] args)
	{
		string output = await ExternalTool.RunAsync("az",
			[.. args, .. inProject ? ProjectArgs : OrgArgs], repoPath, ct);
		return JsonDocument.Parse(output);
	}

	/// <summary>
	/// One REST call the extension has no verb for. A body can only be handed over in a file,
	/// so it is written to a temporary one and deleted afterwards; the log names the route, not
	/// the body, which is review prose and can be a page long.
	/// </summary>
	async Task<JsonDocument> InvokeAsync(string area, string resource, string httpMethod,
		IReadOnlyDictionary<string, string> route, string? jsonBody, CancellationToken ct)
	{
		string? file = null;
		try
		{
			string[] args = [
				"devops", "invoke",
				"--area", area, "--resource", resource,
				"--http-method", httpMethod,
				"--api-version", ApiVersion,
				.. route.Count > 0
					? (string[])["--route-parameters", .. route.Select(p => $"{p.Key}={p.Value}")]
					: [],
			];
			if (jsonBody is not null)
			{
				file = Path.Combine(Path.GetTempPath(), $"stampeded-{Guid.NewGuid():N}.json");
				await File.WriteAllTextAsync(file, jsonBody, ct);
				args = [.. args, "--in-file", file];
			}
			CliLog.Write("host", $"{httpMethod} {area}/{resource} {string.Join(' ', route.Select(p => $"{p.Key}={p.Value}"))}");
			string output = await ExternalTool.RunAsync("az", [.. args, .. OrgArgs], repoPath, ct);
			// A PATCH or POST that returns nothing is a success with no document to read.
			return JsonDocument.Parse(output.Trim().Length == 0 ? "{}" : output);
		}
		finally
		{
			if (file is not null && File.Exists(file))
				File.Delete(file);
		}
	}

	/// <summary>
	/// What `az repos pr show` says, asked once per pull request for the session. The refspec,
	/// the merge state, the reviews and the completion all read from the same answer, and a
	/// round trip each would be four for one screen.
	/// </summary>
	Task<JsonDocument> PrAsync(int number, CancellationToken ct)
	{
		if (!pullRequests.TryGetValue(number, out var pending))
			pullRequests[number] = pending = JsonAsync(ct, inProject: false, "repos", "pr", "show", "--id", number.ToString());
		return pending;
	}

	/// <summary>Forgets the cached answer, so the next reader sees a pull request as it is now:
	/// a vote, a completion or a push changes it.</summary>
	void Forget(int number) => pullRequests.Remove(number);

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

	static string StripRefsHeads(string? refName)
		=> refName is { Length: > 0 } name && name.StartsWith("refs/heads/", StringComparison.Ordinal)
			? name["refs/heads/".Length..]
			: refName ?? "";

	// ---- identity and repository -------------------------------------------------------

	/// <summary>
	/// The account az is signed in as, as the user principal name that
	/// <c>createdBy.uniqueName</c> and <c>reviewers[].uniqueName</c> carry - so "approved" and
	/// "approved by the reader" are compared like with like. Cached: it cannot change without
	/// az being signed in again underneath the app.
	/// </summary>
	public async Task<string> GetViewerLoginAsync(CancellationToken ct = default)
	{
		if (viewerLogin is { Length: > 0 })
			return viewerLogin;
		try
		{
			using var doc = await InvokeAsync("Location", "ConnectionData", "GET",
				new Dictionary<string, string>(), null, ct);
			if (Str(doc.RootElement, "authenticatedUser", "properties", "Account", "$value") is { Length: > 0 } upn)
			{
				CliLog.Write("host", $"viewer {upn} (connection data)");
				return viewerLogin = upn;
			}
		}
		catch (ToolFailedException)
		{
			// Some extension versions do not know that resource name; the signed-in account
			// answers the same question.
		}
		string name = (await ExternalTool.RunAsync("az",
			["account", "show", "--query", "user.name", "--output", "tsv"], repoPath, ct)).Trim();
		CliLog.Write("host", $"viewer {name} (az account)");
		return viewerLogin = name;
	}

	public async Task<string> GetDefaultBranchAsync(CancellationToken ct = default)
		=> StripRefsHeads(Str((await RepositoryAsync(ct)).RootElement, "defaultBranch"));

	/// <summary>What `az repos show` says about the repository: its default branch and its id.
	/// Read once - neither changes while a review is open - and the id is what the policy
	/// commands want, a name being no answer to `--repository-id`.</summary>
	Task<JsonDocument> RepositoryAsync(CancellationToken ct)
		=> repository ??= JsonAsync(ct, inProject: true, "repos", "show", "--repository", repo);

	Task<JsonDocument>? repository;

	public Task<string?> PrUrlAsync(int number, CancellationToken ct = default)
		=> Task.FromResult<string?>($"{WebBase}/pullrequest/{number}");

	public Task<string?> CommitUrlAsync(string sha, CancellationToken ct = default)
		=> Task.FromResult<string?>($"{WebBase}/commit/{sha}");

	string WebBase => $"{orgUrl}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repo)}";

	// ---- the pull request --------------------------------------------------------------

	/// <summary>
	/// Azure DevOps advertises <c>refs/pull/N/merge</c> but no <c>/head</c>, so the head is
	/// fetched from the source branch itself. A push between reading the pull request and
	/// fetching would hand over a newer commit than the one the rest of the review was built
	/// from, so the two are compared and the difference is logged rather than passed over.
	/// </summary>
	public async Task<string> PrHeadRefspecAsync(int number, CancellationToken ct = default)
	{
		var doc = await PrAsync(number, ct);
		if (Node(doc.RootElement, "forkSource") is { ValueKind: not JsonValueKind.Null })
			throw new RefusedException("Pull requests from forks are not supported on Azure DevOps yet.");
		string branch = StripRefsHeads(Str(doc.RootElement, "sourceRefName"));
		if (branch.Length == 0)
			throw new RefusedException($"Azure DevOps did not name a source branch for pull request {number}.");
		return $"+refs/heads/{branch}:refs/stampeded/pr/{number}";
	}

	public async Task<PrDetail> GetPrAsync(int number, CancellationToken ct = default)
	{
		Forget(number);
		var doc = await PrAsync(number, ct);
		var pr = doc.RootElement;
		return new PrDetail(
			number,
			Str(pr, "title") ?? "",
			Str(pr, "description"),
			StripRefsHeads(Str(pr, "targetRefName")),
			StripRefsHeads(Str(pr, "sourceRefName")),
			// active / completed / abandoned. Nothing in the review compares it to GitHub's
			// words; it is shown and logged.
			Str(pr, "status") ?? "",
			new PrAuthor(Str(pr, "createdBy", "uniqueName") ?? Str(pr, "createdBy", "displayName") ?? ""),
			IsDraft: Node(pr, "isDraft")?.ValueKind == JsonValueKind.True);
	}

	public async Task<IReadOnlyList<PrSummary>> ListOpenPrsAsync(CancellationToken ct = default)
	{
		using var doc = await JsonAsync(ct, inProject: true,
			"repos", "pr", "list", "--repository", repo, "--status", "active", "--top", "50");
		var summaries = new List<PrSummary>();
		foreach (var pr in Array(doc.RootElement))
		{
			var reviewers = Array(Node(pr, "reviewers")).ToList();
			// Votes: 10 approved, 5 approved with suggestions, 0 no vote, -5 waiting for the
			// author, -10 rejected. Approval is the two positive ones; anything negative is a
			// reviewer asking for something.
			var latest = reviewers
				.Select(r => (User: Str(r, "uniqueName") ?? Str(r, "displayName") ?? "", Vote: Vote(r)))
				.Where(r => r.Vote != 0)
				.Select(r => new PrLatestReview(new PrAuthor(r.User),
					r.Vote >= 5 ? "APPROVED" : "CHANGES_REQUESTED"))
				.ToList();
			var requested = reviewers
				.Where(r => Vote(r) == 0)
				.Select(r => new PrReviewRequest(Str(r, "uniqueName") ?? Str(r, "displayName")))
				.ToList();
			summaries.Add(new PrSummary(
				Node(pr, "pullRequestId")?.GetInt32() ?? 0,
				Str(pr, "title") ?? "",
				new PrAuthor(Str(pr, "createdBy", "uniqueName") ?? Str(pr, "createdBy", "displayName") ?? ""),
				StripRefsHeads(Str(pr, "sourceRefName")),
				StripRefsHeads(Str(pr, "targetRefName")),
				Node(pr, "isDraft")?.ValueKind == JsonValueKind.True,
				// The list carries no last-touched date, only when the pull request was opened.
				Node(pr, "creationDate")?.GetDateTimeOffset() ?? default,
				// ponytail: no line totals and no check state in the list - both would be a
				// call per row (`az repos pr policy list`). Add them if the list has to show
				// them, one request per row at that point.
				StatusCheckRollup: null,
				HeadRefOid: Str(pr, "lastMergeSourceCommit", "commitId"),
				ReviewDecision: Decision(reviewers),
				LatestReviews: latest,
				ReviewRequests: requested));
		}
		return summaries;
	}

	static int Vote(JsonElement reviewer)
		=> Node(reviewer, "vote") is { ValueKind: JsonValueKind.Number } vote ? vote.GetInt32() : 0;

	/// <summary>
	/// The pull request's standing, in GitHub's word for it. Azure DevOps has no such verdict of
	/// its own: it counts votes and requires the ones its policies mark required, so that is
	/// what is counted here.
	/// </summary>
	static string? Decision(IReadOnlyList<JsonElement> reviewers)
	{
		if (reviewers.Any(r => Vote(r) < 0))
			return "CHANGES_REQUESTED";
		var required = reviewers.Where(r => Node(r, "isRequired")?.ValueKind == JsonValueKind.True).ToList();
		bool anyVote = reviewers.Any(r => Vote(r) != 0);
		return anyVote && required.All(r => Vote(r) >= 5) ? "APPROVED" : null;
	}

	// ---- policies: checks, merge state, merge methods ----------------------------------

	/// <summary>The policy evaluations of a pull request: builds, reviewer counts, linked work
	/// items, resolved comments. Only the builds are checks; the rest is why a merge is
	/// blocked, and is read as such.</summary>
	Task<JsonDocument> PolicyEvaluationsAsync(int number, CancellationToken ct)
		=> JsonAsync(ct, inProject: false, "repos", "pr", "policy", "list", "--id", number.ToString());

	static bool IsBuildPolicy(JsonElement evaluation)
		=> Str(evaluation, "configuration", "type", "displayName") == "Build";

	public async Task<IReadOnlyList<CheckRun>> GetChecksAsync(int number, CancellationToken ct = default)
	{
		using var doc = await PolicyEvaluationsAsync(number, ct);
		var checks = new List<CheckRun>();
		foreach (var evaluation in Array(doc.RootElement).Where(IsBuildPolicy))
		{
			string status = Str(evaluation, "status") ?? "";
			long? buildId = Node(evaluation, "context", "buildId") is { ValueKind: JsonValueKind.Number } id
				? id.GetInt64()
				: null;
			checks.Add(new CheckRun(
				Str(evaluation, "configuration", "settings", "displayName") ?? "Build",
				status,
				Bucket(status),
				buildId is { } build ? $"{orgUrl}/{Uri.EscapeDataString(project)}/_build/results?buildId={build}" : null,
				Workflow: null,
				RunId: buildId));
		}
		return checks;
	}

	/// <summary>
	/// A policy evaluation's status in the words `gh pr checks` uses, which is what a
	/// <see cref="CheckRun.Bucket"/> holds. "notApplicable" is a policy that does not apply to
	/// this pull request at all, which is not a check saying no - it is skipped.
	/// </summary>
	static string Bucket(string status) => status switch {
		"rejected" or "broken" => "fail",
		"queued" or "running" => "pending",
		"notApplicable" => "skipping",
		_ => "pass",
	};

	public async Task<MergeState> GetMergeStateAsync(int number, CancellationToken ct = default)
	{
		Forget(number);
		var pr = (await PrAsync(number, ct)).RootElement;
		using var policies = await PolicyEvaluationsAsync(number, ct);
		var evaluations = Array(policies.RootElement).ToList();
		var builds = evaluations.Where(IsBuildPolicy).ToList();
		string mergeStatus = Str(pr, "mergeStatus") ?? "";
		bool isDraft = Node(pr, "isDraft")?.ValueKind == JsonValueKind.True;
		// Only a blocking policy refuses the completion; an optional build that failed is a
		// check the reader may take or leave, which is what UNSTABLE says on the other host.
		bool Unsettled(JsonElement e) => Bucket(Str(e, "status") ?? "") is "fail" or "pending";
		bool blocked = evaluations.Any(e =>
			Node(e, "configuration", "isBlocking")?.ValueKind == JsonValueKind.True && Unsettled(e));
		bool unstable = !blocked && builds.Any(Unsettled);
		// The checks are handed over in the shape the panes already fold, so one reading of a
		// rollup serves both hosts.
		string rollup = JsonSerializer.Serialize(builds.Select(b => new {
			name = Str(b, "configuration", "settings", "displayName") ?? "Build",
			state = Str(b, "status") ?? "",
			conclusion = Bucket(Str(b, "status") ?? "") switch {
				"fail" => "FAILURE",
				"pending" => "PENDING",
				_ => "SUCCESS",
			},
		}));
		using var rollupDoc = JsonDocument.Parse(rollup);
		return new MergeState(
			mergeStatus switch {
				"succeeded" => "MERGEABLE",
				"conflicts" => "CONFLICTING",
				_ => "UNKNOWN",
			},
			isDraft ? "DRAFT" : blocked ? "BLOCKED" : unstable ? "UNSTABLE" : "CLEAN",
			Decision([.. Array(Node(pr, "reviewers"))]),
			isDraft,
			StripRefsHeads(Str(pr, "targetRefName")),
			rollupDoc.RootElement.Clone(),
			Str(pr, "status"),
			Str(pr, "lastMergeSourceCommit", "commitId")) {
			Host = Name,
		};
	}

	/// <summary>The merge strategies the target branch's policy allows. Without such a policy
	/// all three are on, which is what Azure DevOps itself does.</summary>
	public async Task<MergeMethods> GetMergeMethodsAsync(CancellationToken ct = default)
	{
		if (mergeMethods is { } known)
			return known;
		var repository = (await RepositoryAsync(ct)).RootElement;
		string branch = StripRefsHeads(Str(repository, "defaultBranch"));
		using var doc = await JsonAsync(ct, inProject: true, "repos", "policy", "list",
			"--repository-id", Str(repository, "id") ?? repo, "--branch", branch);
		// fa4e907d-c16b-4a4c-9dfa-4916e5d171ab is the "Require a merge strategy" policy type.
		var strategy = Array(doc.RootElement).FirstOrDefault(p =>
			string.Equals(Str(p, "type", "id"), "fa4e907d-c16b-4a4c-9dfa-4916e5d171ab",
				StringComparison.OrdinalIgnoreCase));
		if (strategy.ValueKind != JsonValueKind.Object)
			return mergeMethods = new MergeMethods(true, true, true);
		bool On(string setting) => Node(strategy, "settings", setting)?.ValueKind == JsonValueKind.True;
		return mergeMethods = new MergeMethods(On("allowNoFastForward"), On("allowSquash"), On("allowRebase"));
	}

	// ---- work items ---------------------------------------------------------------------

	/// <summary>The title of a work item, or null when the number is not one. Azure DevOps
	/// autolinks "#123" to a work item exactly as GitHub does to an issue.</summary>
	public Task<string?> GetIssueTitleAsync(int number, CancellationToken ct = default)
	{
		if (!workItemTitles.TryGetValue(number, out var pending))
			workItemTitles[number] = pending = AskAsync();
		return pending;

		async Task<string?> AskAsync()
		{
			try
			{
				string title = (await ExternalTool.RunAsync("az",
					["boards", "work-item", "show", "--id", number.ToString(),
						"--query", "fields.\"System.Title\"", "--output", "tsv",
						"--organization", orgUrl], repoPath, ct)).Trim();
				return title.Length > 0 ? title : null;
			}
			catch (ToolFailedException)
			{
				// Not a work item, which is the answer rather than a failure.
				return null;
			}
		}
	}

	public Task<string?> GetIssueUrlPrefixAsync(CancellationToken ct = default)
		=> Task.FromResult<string?>($"{orgUrl}/{Uri.EscapeDataString(project)}/_workitems/edit/");

	// ---- completing ---------------------------------------------------------------------

	/// <summary>
	/// Completes the pull request. <c>az repos pr update</c> knows only <c>--squash</c>, so the
	/// strategy is set through the REST PATCH instead. The head commit is named in it: Azure
	/// DevOps refuses a completion that names a commit the branch has moved past, which is the
	/// guard a merge wants.
	/// </summary>
	public async Task<string> MergePrAsync(int number, string method, bool deleteBranch = false,
		CancellationToken ct = default)
	{
		Forget(number);
		var pr = (await PrAsync(number, ct)).RootElement;
		string? head = Str(pr, "lastMergeSourceCommit", "commitId");
		if (head is not { Length: > 0 })
			throw new RefusedException($"Azure DevOps did not name a head commit for pull request {number}.");
		string body = JsonSerializer.Serialize(new {
			status = "completed",
			lastMergeSourceCommit = new { commitId = head },
			completionOptions = new {
				mergeStrategy = method switch {
					"squash" => "squash",
					"rebase" => "rebase",
					_ => "noFastForward",
				},
				deleteSourceBranch = deleteBranch,
			},
		});
		using var result = await InvokeAsync("git", "pullRequests", "PATCH", new Dictionary<string, string> {
			["project"] = project,
			["repositoryId"] = repo,
			["pullRequestId"] = number.ToString(),
		}, body, ct);
		Forget(number);
		return $"completed with {method}";
	}

	/// <summary>Takes the pull request out of draft, which is what makes Azure DevOps start
	/// evaluating its policies and asking its reviewers.</summary>
	public async Task<string> MarkReadyForReviewAsync(int number, CancellationToken ct = default)
	{
		Forget(number);
		using var doc = await JsonAsync(ct, inProject: false,
			"repos", "pr", "update", "--id", number.ToString(), "--draft", "false");
		return $"pull request {number} is ready for review";
	}

	/// <summary>Azure DevOps has no server-side update-branch: the branch is rebased where it
	/// is checked out and pushed.</summary>
	public Task UpdateBranchAsync(int number, CancellationToken ct = default)
		=> throw new RefusedException(
			"Azure DevOps has no server-side update-branch; rebase the branch locally and push.");

	// ---- build logs ----------------------------------------------------------------------

	/// <summary>
	/// The logs of the failed steps of a build, in the shape `gh run view --log-failed` gives:
	/// each failed record's log under a heading naming it. A build's timeline is what says
	/// which records failed and where their logs are.
	/// </summary>
	public async Task<string> GetFailedLogAsync(long runId, CancellationToken ct = default)
	{
		using var timeline = await InvokeAsync("build", "timeline", "GET", new Dictionary<string, string> {
			["project"] = project,
			["buildId"] = runId.ToString(),
		}, null, ct);
		var text = new System.Text.StringBuilder();
		foreach (var record in Array(Node(timeline.RootElement, "records")))
		{
			if (Str(record, "result") != "failed" || Node(record, "log", "id") is not { } logId)
				continue;
			text.AppendLine($"### {Str(record, "name") ?? "(unnamed step)"}");
			using var log = await InvokeAsync("build", "logs", "GET", new Dictionary<string, string> {
				["project"] = project,
				["buildId"] = runId.ToString(),
				["logId"] = logId.GetInt32().ToString(),
			}, null, ct);
			foreach (var line in Array(Node(log.RootElement, "value")))
				text.AppendLine(line.GetString());
			text.AppendLine();
		}
		return text.Length > 0 ? text.ToString() : "No failed step in this build has a log.";
	}

	// ---- threads: comments, replies, resolution ------------------------------------------

	/// <summary>
	/// Azure DevOps numbers a thread's comments from 1 again in every thread, and the review
	/// carries one number per comment. The two are packed into it and split apart wherever a
	/// reply or a resolution has to name the thread again.
	/// </summary>
	public static long PackId(int threadId, int commentId) => threadId * 1_000_000L + commentId;

	public static (int Thread, int Comment) SplitId(long packed)
		=> ((int)(packed / 1_000_000L), (int)(packed % 1_000_000L));

	Task<JsonDocument> ThreadsAsync(int number, CancellationToken ct)
		=> InvokeAsync("git", "pullRequestThreads", "GET", new Dictionary<string, string> {
			["project"] = project,
			["repositoryId"] = repo,
			["pullRequestId"] = number.ToString(),
		}, null, ct);

	public async Task<IReadOnlyList<PostedComment>> GetReviewCommentsAsync(int number,
		CancellationToken ct = default)
	{
		using var doc = await ThreadsAsync(number, ct);
		string? prUrl = await PrUrlAsync(number, ct);
		var iterationCommits = await IterationCommitsAsync(number, ct);
		var comments = new List<PostedComment>();
		foreach (var thread in Array(Node(doc.RootElement, "value")))
		{
			// A thread with no file is the pull request's own conversation, which this review
			// does not show - as it does not show GitHub's issue comments.
			if (Node(thread, "threadContext", "filePath") is not { ValueKind: JsonValueKind.String } pathNode)
				continue;
			int threadId = Node(thread, "id")?.GetInt32() ?? 0;
			string path = (pathNode.GetString() ?? "").TrimStart('/');
			var right = Node(thread, "threadContext", "rightFileStart", "line");
			var left = Node(thread, "threadContext", "leftFileStart", "line");
			string side = right is not null ? "RIGHT" : "LEFT";
			int? line = (right ?? left)?.GetInt32();
			// Which revision the thread was written against: Azure DevOps names it by iteration
			// rather than by commit, and the iterations say which commit each one was.
			string? commit = Node(thread, "pullRequestThreadContext", "iterationContext",
				"secondComparingIteration") is { ValueKind: JsonValueKind.Number } iteration
					&& iterationCommits.TryGetValue(iteration.GetInt32(), out string? sha)
				? sha
				: null;
			foreach (var comment in Array(Node(thread, "comments")))
			{
				if (Str(comment, "commentType") != "text"
					|| Node(comment, "isDeleted")?.ValueKind == JsonValueKind.True)
					continue;
				comments.Add(new PostedComment(
					PackId(threadId, Node(comment, "id")?.GetInt32() ?? 0),
					Str(comment, "content") ?? "",
					path,
					line,
					side,
					new PostedUser(Str(comment, "author", "uniqueName")
						?? Str(comment, "author", "displayName") ?? ""),
					// Azure DevOps tracks a thread's line across iterations itself, so the line
					// above is the line as the change stands now and never has to be located in
					// a hunk the way a GitHub comment's original line does.
					OriginalLine: line,
					DiffHunk: null,
					OriginalCommitId: commit,
					HtmlUrl: prUrl is null ? null : $"{prUrl}?discussionId={threadId}"));
			}
		}
		return comments;
	}

	/// <summary>Which commit each iteration of the pull request was, so a thread pinned to an
	/// iteration can name the revision it was written against.</summary>
	async Task<Dictionary<int, string>> IterationCommitsAsync(int number, CancellationToken ct)
	{
		using var doc = await InvokeAsync("git", "pullRequestIterations", "GET", new Dictionary<string, string> {
			["project"] = project,
			["repositoryId"] = repo,
			["pullRequestId"] = number.ToString(),
		}, null, ct);
		var commits = new Dictionary<int, string>();
		foreach (var iteration in Array(Node(doc.RootElement, "value")))
		{
			if (Node(iteration, "id")?.GetInt32() is { } id
				&& Str(iteration, "sourceRefCommit", "commitId") is { Length: > 0 } sha)
				commits[id] = sha;
		}
		return commits;
	}

	/// <summary>
	/// Every vote cast on the pull request, as a review each. Azure DevOps does not record which
	/// commit a vote was cast on - its "reset votes on push" policy is how a repository makes a
	/// vote mean the head it was cast on - so the overview's stale-review marker never shows.
	/// </summary>
	public async Task<IReadOnlyList<PrReview>> GetReviewsAsync(int number, CancellationToken ct = default)
	{
		Forget(number);
		var pr = (await PrAsync(number, ct)).RootElement;
		return [.. Array(Node(pr, "reviewers"))
			.Where(r => Vote(r) != 0)
			.Select(r => new PrReview(
				new PostedUser(Str(r, "uniqueName") ?? Str(r, "displayName") ?? ""),
				Vote(r) >= 5 ? "APPROVED" : "CHANGES_REQUESTED",
				CommitId: null,
				SubmittedAt: null))];
	}

	public async Task<IReadOnlyList<ThreadResolution>> GetThreadResolutionsAsync(int number,
		CancellationToken ct = default)
	{
		using var doc = await ThreadsAsync(number, ct);
		var resolutions = new List<ThreadResolution>();
		foreach (var thread in Array(Node(doc.RootElement, "value")))
		{
			int threadId = Node(thread, "id")?.GetInt32() ?? 0;
			var ids = Array(Node(thread, "comments"))
				.Where(c => Str(c, "commentType") == "text")
				.Select(c => PackId(threadId, Node(c, "id")?.GetInt32() ?? 0))
				.ToList();
			resolutions.Add(new ThreadResolution(
				$"{number}/{threadId}",
				Str(thread, "status") is "fixed" or "closed" or "wontFix" or "byDesign",
				ids));
		}
		return resolutions;
	}

	/// <summary>
	/// Resolving a thread names the pull request as well as the thread, and the review hands a
	/// resolution nothing but the thread id it was given - so the id it is given is both,
	/// written "pullRequest/thread". GitHub's thread id is an opaque string too, which is why
	/// this fits through the same member.
	/// </summary>
	public async Task SetThreadResolvedAsync(string threadId, bool resolved, CancellationToken ct = default)
	{
		string[] parts = threadId.Split('/');
		if (parts.Length != 2)
			throw new RefusedException($"Not an Azure DevOps thread id: {threadId}");
		string body = JsonSerializer.Serialize(new { status = resolved ? "fixed" : "active" });
		using var _ = await InvokeAsync("git", "pullRequestThreads", "PATCH", new Dictionary<string, string> {
			["project"] = project,
			["repositoryId"] = repo,
			["pullRequestId"] = parts[0],
			["threadId"] = parts[1],
		}, body, ct);
	}

	// ---- writing -------------------------------------------------------------------------

	/// <summary>
	/// Azure DevOps has no review object: a review is its comments and a vote. The comments are
	/// posted first, so a failure never leaves a vote standing with no reasons written down; a
	/// failure part-way says how many went through, which is what tells the reader what to look
	/// for on the site.
	/// </summary>
	public async Task SubmitReviewAsync(int number, ReviewSubmission submission,
		CancellationToken ct = default)
	{
		var attributed = ReviewAttribution.Attributed(submission);
		int posted = 0;
		try
		{
			foreach (var comment in attributed.Comments)
			{
				await PostThreadAsync(number, comment.Body, comment.Path, comment.Line, comment.Side, ct);
				posted++;
			}
			if (attributed.Body.Trim().Length > 0)
			{
				await PostThreadAsync(number, attributed.Body, path: null, line: null, side: null, ct);
				posted++;
			}
		}
		catch (Exception)
		{
			if (posted > 0)
			{
				CliLog.Write("host",
					$"{posted} comment(s) were posted before the failure; no vote was cast");
			}
			throw;
		}
		string? vote = attributed.Event switch {
			// "reject" is stronger than GitHub's request-changes: it blocks completion outright
			// where a request for changes is a reviewer asking. "wait-for-author" is the one
			// that means the same thing.
			"APPROVE" => "approve",
			"REQUEST_CHANGES" => "wait-for-author",
			_ => null,
		};
		if (vote is null)
			return;
		Forget(number);
		await ExternalTool.RunAsync("az",
			["repos", "pr", "set-vote", "--id", number.ToString(), "--vote", vote, .. OrgArgs], repoPath, ct);
	}

	async Task PostThreadAsync(int number, string body, string? path, int? line, string? side,
		CancellationToken ct)
	{
		var comments = new[] { new { content = body, commentType = "text" } };
		// The side decides which pair of positions the thread carries: a comment on a removed
		// line belongs to the left file, one on anything else to the right. Azure DevOps reads
		// a thread with neither as the pull request's own conversation, which is what a review
		// body is.
		var position = new { line = line ?? 1, offset = 1 };
		var context = new Dictionary<string, object> { ["filePath"] = "/" + path };
		if (side == "LEFT")
		{
			context["leftFileStart"] = position;
			context["leftFileEnd"] = position;
		}
		else
		{
			context["rightFileStart"] = position;
			context["rightFileEnd"] = position;
		}
		object thread = path is null
			? new { comments, status = "active" }
			: new { comments, status = "active", threadContext = context };
		using var _ = await InvokeAsync("git", "pullRequestThreads", "POST", new Dictionary<string, string> {
			["project"] = project,
			["repositoryId"] = repo,
			["pullRequestId"] = number.ToString(),
		}, JsonSerializer.Serialize(thread), ct);
	}

	public async Task ReplyToCommentAsync(int number, long commentId, string body,
		CancellationToken ct = default)
	{
		var (thread, comment) = SplitId(commentId);
		string json = JsonSerializer.Serialize(new {
			content = body,
			parentCommentId = comment,
			commentType = "text",
		});
		using var _ = await InvokeAsync("git", "pullRequestThreadComments", "POST",
			new Dictionary<string, string> {
				["project"] = project,
				["repositoryId"] = repo,
				["pullRequestId"] = number.ToString(),
				["threadId"] = thread.ToString(),
			}, json, ct);
	}

	// ---- merge queue ---------------------------------------------------------------------

	/// <summary>Nothing on Azure DevOps empties the queue on its own, so whichever window is
	/// open drains it - as on a GitHub repository without the drainer workflow.</summary>
	public Task<bool> HasMergeQueueWorkflowAsync(CancellationToken ct = default) => Task.FromResult(false);

	public Task DispatchMergeQueueAsync(CancellationToken ct = default) => Task.CompletedTask;
}
