# Pull-request hosts and review state

`src/Stampeded.Core/{PullRequests,GitHub,AzureDevOps,MergeQueue,Review}`.

## Shape of the layer

```
PullRequestHosts.ForAsync(repoPath)      <- decided once per workspace, from origin's URL
        |
        v
IPullRequestHost                          <- the only vocabulary above this line is GitHub's
   |-- GitHubService(repoPath)                      over `gh`
   \-- AzureDevOpsService(repoPath, org, project, repo)   over `az` + the azure-devops extension

PrCache           <- what only the host knows, on disk, so a review opens offline
ReviewStateStore  <- what the *reader* did, on disk, per review
CommentAnchor     <- glue that survives a force-push
MergeQueueService <- a queue in a git ref, driven through IPullRequestHost
```

Everything outside the two implementations speaks GitHub: `APPROVE` / `REQUEST_CHANGES` /
`COMMENT`, `APPROVED` / `CHANGES_REQUESTED`, `LEFT` / `RIGHT`, `MERGEABLE` / `CONFLICTING`, `CLEAN`
/ `UNSTABLE` / `BLOCKED` / `BEHIND` / `DIRTY` / `DRAFT`. Azure DevOps translates into that inside
`AzureDevOpsService` and nowhere else. The only thing a pane may say out loud about which host
answered is `IPullRequestHost.Name`, surfaced as `ReviewWorkspace.HostName`.

### Host selection

`PullRequestHosts.ForAsync` (`PullRequestHosts.cs:16`):

1. `git config --get remote.origin.url`, with **exit 1 accepted** (`:39`) - a clone that was never
   pushed is an answer, not a failure.
2. `AzureDevOpsUrl.TryParse(origin, ...)` decides. Anything it does not parse is GitHub **on
   purpose**: `gh` also serves GitHub Enterprise hosts, which cannot be enumerated here.
3. `STAMPEDED_PR_HOST=github|azdo` overrides (`:19`). Note the override still uses the org/project/
   repo the parse produced - forcing `azdo` on a GitHub clone constructs
   `AzureDevOpsService("", "", "")` and every command will fail.

## IPullRequestHost - the contract

| Member | Meaning | GitHub | Azure DevOps |
| --- | --- | --- | --- |
| `Name` | "GitHub" / "Azure DevOps" | const | const |
| `AcceptsOwnApproval` | whether the host takes a verdict from the PR's own author | `false` | `true` |
| `PrHeadRefspecAsync(int)` | refspec fetching PR head into `refs/stampeded/pr/N` | `+refs/pull/N/head:refs/stampeded/pr/N` - synchronous, always works, fork or not | reads `az repos pr show`, **refuses forks**, uses `+refs/heads/<sourceBranch>:refs/stampeded/pr/N` |
| `PrUrlAsync` / `CommitUrlAsync` | browser URL, `null` when the repo is not on this host | `gh repo view --json nameWithOwner` | computed from org/project/repo, never null |
| `GetViewerLoginAsync()` | the account the CLI is signed in as | `gh api user --jq .login`, cached | `az devops invoke Location/ConnectionData` -> `authenticatedUser.properties.Account.$value` (a UPN), falling back to `az account show` |
| `GetDefaultBranchAsync()` | authority for the default branch | `gh repo view --json defaultBranchRef` | `az repos show` -> `defaultBranch`, `refs/heads/` stripped |
| `ListOpenPrsAsync()` | the open list | one `gh pr list` with 16 JSON fields | `az repos pr list --status active --top 50`; **no line totals, no check state** |
| `GetPrAsync(int)` | title, body, branches, state, author, draft | `gh pr view N --json ...` | `az repos pr show --id N` |
| `GetChecksAsync(int)` | check runs | `gh pr checks N --json name,state,link,bucket,workflow` | build **policy evaluations** only |
| `GetMergeStateAsync(int)` | would the host merge right now | `gh pr view N --json mergeable,mergeStateStatus,...` | `pr show` + `pr policy list`, folded |
| `GetIssueTitleAsync(int)` | title of `#N`, or `null` | `gh api repos/{owner}/{repo}/issues/N`; 404 is the answer | `az boards work-item show --id N` |
| `GetIssueUrlPrefixAsync()` | prefix for autolinking `#N` | `.../issues/` | `.../_workitems/edit/` |
| `GetMergeMethodsAsync()` | which merge strategies the repo allows | `gh repo view --json mergeCommitAllowed,...`, cached | the target branch's "Require a merge strategy" policy; **all three on when there is none** |
| `MergePrAsync(int, method, deleteBranch)` | `method` is a *gh flag name* - `merge`, `squash`, `rebase` | `gh pr merge N --{method} [--delete-branch]` | REST `PATCH` with `completionOptions.mergeStrategy` (`noFastForward`/`squash`/`rebase`) |
| `MarkReadyForReviewAsync(int)` | out of draft | `gh pr ready N` | `az repos pr update --id N --draft false` |
| `GetFailedLogAsync(long runId)` | log of the failed steps | `gh run view <id> --log-failed` | build timeline + per-record logs, assembled into the same shape |
| `GetReviewCommentsAsync(int)` | posted line comments | `gh api .../pulls/N/comments --paginate` | PR threads with a `threadContext.filePath` |
| `GetReviewsAsync(int)` | submitted reviews, oldest first | `gh api .../pulls/N/reviews --paginate` | **synthesized from votes** |
| `GetThreadResolutionsAsync(int)` | thread resolution + the REST ids of each thread's comments | GraphQL `reviewThreads` | thread `status` |
| `SetThreadResolvedAsync(threadId, bool)` | resolve/unresolve | GraphQL mutation with an opaque node id | id is `"{prNumber}/{threadId}"`, split apart again |
| `UpdateBranchAsync(int)` | rebase PR branch onto target **on the server** | `gh api -X PUT .../pulls/N/update-branch -f update_method=rebase` | **throws `RefusedException`** - no such API |
| `SubmitReviewAsync(int, ReviewSubmission)` | a verdict plus its line comments | one POST to `.../pulls/N/reviews` | comments first, then a vote - there is no review object |
| `ReplyToCommentAsync(int, long, string)` | answer inside an existing thread | POST `.../pulls/N/comments/{id}/replies` | POST into the thread, `parentCommentId` |
| `HasMergeQueueWorkflowAsync()` | does something on the host drain the queue | looks for `stampeded-merge-queue.yml` among active workflows | always `false` |
| `DispatchMergeQueueAsync()` | wake that drainer | `repository_dispatch` with `event_type=stampeded-merge-queue` | no-op |

### The error model

Two exception types, both meaning "it did not happen, and the message says why":
`ToolFailedException` (a CLI said no; has exit code and stderr) and `RefusedException` (*this tool*
said no, before running anything). `ExternalTool.RunAsync` converts a `Win32Exception` (the binary
is not installed) into `ToolFailedException(exe, -1, ...)`, so a missing `gh` never escapes
unhandled and leaves a pane spinning.

## The data model

### Small records

| Record | Meaning |
| --- | --- |
| `PrAuthor(Login)` | a user. On Azure DevOps the "login" is a UPN. |
| `PrLatestReview(Author?, State?)` | one reviewer's last word |
| `PrRepoOwner(Login)` | owner of the *head* repository - the fork test |
| `PrReviewRequest(Login?)` | `Login` is nullable **because a team has none**: `reviewRequests` holds both, and gh names the team, not its members |
| `PrDetail(Number, Title, Body, BaseRefName, HeadRefName, State, Author, IsDraft)` | `State` is GitHub's `OPEN`/`CLOSED`/`MERGED`; Azure DevOps puts `active`/`completed`/`abandoned` here and *nothing compares it* |
| `CheckRun(Name, State, Bucket, Link, Workflow, RunId?)` | `Bucket` is `gh pr checks`'s own word: `pass`/`fail`/`pending`/`skipping`/`cancel`. `RunId` is `null` for a check reported from somewhere neither CLI can read |
| `MergeMethods(...)` | `Allowed` returns the *gh flag names* `merge`/`squash`/`rebase`, in GitHub's own menu order |
| `ThreadResolution(ThreadId, IsResolved, CommentIds)` | `ThreadId` is opaque - a GraphQL node id on GitHub, `"pr/thread"` on Azure DevOps |
| `ReviewCommentDto(Path, Line, Side, Body)` | one line comment of a submission |
| `ReviewSubmission(Body, Event, Comments)` | `Event` is `APPROVE` / `REQUEST_CHANGES` / `COMMENT` |

### PrSummary

Positional fields map 1:1 onto `gh pr list --json`. Two are stamped on **after** the list is read
and are `init`-only:

- `ViewerLogin` - without it, "approved" cannot be told from "approved by *me*".
- `OriginOwner` - without it, a head branch name means nothing: a PR names the branch as it is
  called in the repository it lives in, which for a fork is not this one.

```csharp
public bool HeadIsFork => OriginOwner is { Length: > 0 } origin
    && HeadRepositoryOwner is { Login.Length: > 0 } head
    && !string.Equals(head.Login, origin, StringComparison.OrdinalIgnoreCase);
```

Owner alone decides it - a fork cannot sit beside its original under the same account.

`ApprovedByMe` is read from `LatestReviews`, **not** from `ReviewDecision`: a PR can be approved
without your vote and voted on without being approved. `ReviewRequestedFromMe` is by name only; a
team request is nobody's in particular.

### CheckRollup - one folding, two panes

A PR's status-check rollup arrives as one array mixing two kinds: modern check runs (`conclusion`)
and the older status contexts (`state`). It is read in exactly one place because the PR list and the
merge state fold it the same way, and two foldings that drift apart would show the same review green
in one pane and failing in another.

```csharp
public static string Verdict(JsonElement item)
{
    string? conclusion = ...; string? state = ...;
    return ((conclusion is { Length: > 0 } ? conclusion : state) ?? "").ToUpperInvariant() switch {
        "FAILURE" or "ERROR" or "TIMED_OUT" or "STARTUP_FAILURE" or "CANCELLED" or "ACTION_REQUIRED" => "fail",
        "" or "PENDING" or "IN_PROGRESS" or "QUEUED" or "EXPECTED" or "WAITING" or "REQUESTED" => "pending",
        _ => "green",
    };
}
```

`conclusion` wins over `state`. **Anything not named is green** - `SUCCESS`, but also `SKIPPED` and
`NEUTRAL`, which are not a check saying no. `CANCELLED` deliberately counts as a failure.
`Bucket` is worst-first: any fail -> `"fail"`; else any pending -> `"pending"`; else `"green"` if
there was anything at all, otherwise `"none"`.

### MergeState

```csharp
public sealed record MergeState(string? Mergeable, string? MergeStateStatus, string? ReviewDecision = null,
    bool IsDraft = false, string? BaseRefName = null, JsonElement? StatusCheckRollup = null,
    string? State = null, string? HeadRefOid = null)
{
    public string Host { get; init; } = "GitHub";
    public bool CanMerge => Mergeable == "MERGEABLE"
        && MergeStateStatus is "CLEAN" or "UNSTABLE" or "HAS_HOOKS";
```

`UNSTABLE` is a failing or pending check on a PR GitHub *would still merge* - the reader's call, not
a refusal. `UNKNOWN` is what GitHub answers without push access, and offering a button that will be
rejected is worse than not offering one. `State` and `HeadRefOid` exist for the merge queue: whether
somebody merged or closed it since, and whether the branch still carries the queued revision.

`Summary` gives at most **two** reasons, ordered by what the reader would do: fix first (conflicts,
draft), then wait (behind, failing checks, running checks), then what someone else owes (changes
requested, no approving review). `Explain` is the long form, one line per reason.

**`Host` is why both read `{Host}` and not `"GitHub"`.** It is `init`-only and defaults to
`"GitHub"`; Azure DevOps sets it. A third host that forgot would silently say "GitHub".

### PostedComment

```csharp
public sealed record PostedComment(long Id, string Body, string Path, int? Line, string? Side, PostedUser? User,
    [property: JsonPropertyName("original_line")] int? OriginalLine,
    [property: JsonPropertyName("diff_hunk")] string? DiffHunk,
    [property: JsonPropertyName("original_commit_id")] string? OriginalCommitId,
    [property: JsonPropertyName("html_url")] string? HtmlUrl = null);
```

`Line` is `null` once GitHub has stopped tracking the comment (the diff moved past it) - that is the
trigger for the whole `CommentAnchor` path. `OriginalLine` + `DiffHunk` are what survive;
`OriginalCommitId` is the commit the comment was written against, still in the object database
whenever that head was ever fetched, which is what lets the member-relocation path work.

`PrReview.CommitId` is what makes the overview's **stale-review** marker possible - a verdict given
on a head that is no longer current. Azure DevOps sets it `null`.

## GitHubService - exactly what it runs

Constructed with `repoPath`; every command runs with that working directory so `gh` resolves the
repo from origin and fills in the `{owner}`/`{repo}` placeholders itself. There is no token of its
own - auth, SSO and refresh ride on `gh auth`.

Serialization is a source-generated context over `JsonSerializerDefaults.Web` +
`PropertyNameCaseInsensitive`, so gh's camelCase JSON lands on the PascalCase records without
attributes, and the snake_case REST fields carry explicit `[JsonPropertyName]`s.

**Cached for the process lifetime** (they cannot change without gh being re-authenticated or the
repo settings edited underneath): `viewerLogin`, `defaultBranch`, `mergeMethods`, `ownerRepo`,
`hasMergeQueueWorkflow`, and the per-number `issueTitles` memo.

### The two GraphQL calls

REST does not expose thread resolution, so `GetThreadResolutionsAsync` issues:

```graphql
query($owner: String!, $repo: String!, $number: Int!) {
  repository(owner: $owner, name: $repo) {
    pullRequest(number: $number) {
      reviewThreads(first: 100) {
        nodes { id isResolved comments(first: 50) { nodes { databaseId } } }
      }
    }
  }
}
```

via `gh api graphql -f query=... -f owner=... -f repo=... -F number=N` (`-F` for the typed Int). It
walks the result with hard `GetProperty` calls - **any shape change throws `KeyNotFoundException`,
not a `ToolFailedException`**. The `first: 100` / `first: 50` caps are silent: a PR with more than
100 threads loses resolution state on the rest.

`SetThreadResolvedAsync` posts `resolveReviewThread` / `unresolveReviewThread`.

### Notable details

- **`gh pr checks` exits non-zero when checks failed or are pending.** So `GetChecksAsync` drops out
  of `ExternalTool.RunAsync` and uses CliWrap directly with `CommandResultValidation.None`, taking
  the JSON from stdout regardless of exit code, and only throwing when stdout is *empty* and the
  exit code is non-zero.
- **Run ids come from the link, by regex**: `[GeneratedRegex(@"/actions/runs/(\d+)")]`. Only a
  GitHub Actions link carries one; a check reported by anything else opens nothing.
- **Issue titles memoize the *task*, not the answer** - a description rebuilt twice in a row would
  otherwise ask about every number twice before either reply arrived. The dictionary is unlocked
  because the callers are all on the UI thread. A 404 is the *answer* (`#141414` is a colour), not a
  failure.
- **Reply is its own request**: a review submission's comments take only a path and a line, so a
  comment meant as an answer would start a *new* thread on that line. A reply cannot be batched into
  a pending review.

## AzureDevOpsService - mapping onto GitHub's vocabulary

Every command names the organization explicitly rather than leaning on `az devops configure
--defaults`, which is a per-machine setting a reader may have pointed at another project entirely.

```csharp
const string ApiVersion = "7.1";
readonly string orgUrl = $"https://dev.azure.com/{org}";
string[] OrgArgs     => ["--organization", orgUrl, "--output", "json"];
string[] ProjectArgs => [.. OrgArgs, "--project", project];
```

`--project` is **not** universal: the commands addressed by pull-request id (`pr show`, `pr policy
list`, `pr set-vote`, `pr update`) and `az devops invoke` reject it.

`InvokeAsync(area, resource, httpMethod, route, jsonBody, ct)` is the `gh api` analogue. A body can
only be handed to `az` **in a file**, so it is written to `%TEMP%/stampeded-<guid>.json` and deleted
in a `finally`. The log names the route, never the body - review prose can be a page long. An empty
response is normalized to `{}` before parsing.

### The mappings

**Votes -> GitHub review states.** Azure DevOps scores a reviewer from 10 to -10:

| Vote | Meaning | Mapped to |
| --- | --- | --- |
| `10` | approved | `APPROVED` |
| `5` | approved with suggestions | `APPROVED` |
| `0` | no vote | *not a review* - becomes a `PrReviewRequest` instead |
| `-5` | waiting for the author | `CHANGES_REQUESTED` |
| `-10` | rejected | `CHANGES_REQUESTED` |

**No review object -> votes as reviews.** `GetReviewsAsync` manufactures one `PrReview` per non-zero
vote with `CommitId: null` and `SubmittedAt: null`. `CommitId: null` is load-bearing: Azure DevOps
does not record which commit a vote was cast on - its "reset votes on push" policy is how a
repository makes a vote mean the head it was cast on - **so the overview's stale-review marker never
shows on Azure DevOps.**

**No review decision -> counted votes.** `Decision(reviewers)`: any negative vote gives
`CHANGES_REQUESTED`; otherwise `APPROVED` when something voted and every *required* reviewer is at
5 or better; otherwise `null`. It never returns `"REVIEW_REQUIRED"`, so `MergeState.Summary`'s "no
approving review yet" branch is unreachable on this host.

**Policies -> checks and merge state.** Only evaluations whose `configuration.type.displayName ==
"Build"` are checks; the rest (reviewer counts, linked work items, resolved comments) are *why a
merge is blocked*. `Bucket(status)`: `rejected`/`broken` -> `fail`; `queued`/`running` -> `pending`;
`notApplicable` -> `skipping` (a policy that does not apply is not a check saying no); else `pass`.

`GetMergeStateAsync` is the densest translation in the file:

- `mergeStatus`: `succeeded` -> `MERGEABLE`, `conflicts` -> `CONFLICTING`, else `UNKNOWN`.
- `mergeStateStatus` is synthesized: `isDraft` -> `DRAFT`; else any **blocking** policy unsettled ->
  `BLOCKED`; else any **build** policy unsettled -> `UNSTABLE`; else `CLEAN`. The blocking/optional
  split is exactly GitHub's `BLOCKED` vs `UNSTABLE` distinction.
- The checks are re-serialized into a **fake GitHub rollup** so `CheckRollup` folds both hosts
  identically.
- There is **no `BEHIND` and no `HAS_HOOKS`** on this host, so those branches are dead here.

**Completion.** `az repos pr update` knows only `--squash`, so `MergePrAsync` PATCHes instead, naming
`lastMergeSourceCommit` - Azure DevOps refuses a completion that names a commit the branch has moved
past, which is the guard a merge wants.

**Threads -> comments.** `GetReviewCommentsAsync` GETs `git/pullRequestThreads` and:

- skips any thread with no `threadContext.filePath` - that is the PR's own conversation, which this
  review does not show, as it does not show GitHub's issue comments;
- `rightFileStart` present -> `Side = "RIGHT"`, else `"LEFT"`;
- keeps only `commentType == "text"` and not `isDeleted`;
- sets `OriginalLine: line` and `DiffHunk: null` - **Azure DevOps tracks a thread's line across
  iterations itself**, so the line is always current. This is why the `CommentAnchor` fallback path
  is effectively GitHub-only.

**Comment ids are packed.** Azure DevOps numbers a thread's comments from 1 again in every thread,
and `PostedComment.Id` is one `long`:

```csharp
public static long PackId(int threadId, int commentId) => threadId * 1_000_000L + commentId;
public static (int Thread, int Comment) SplitId(long packed)
    => ((int)(packed / 1_000_000L), (int)(packed % 1_000_000L));
```

Pinned by `AzureDevOpsIdTests.cs`. The ceiling is 999 999 comments per thread; nothing checks it.

**Submitting a review** is a sequence, because there is no review object: attribute, post each line
comment as its own thread (counting), post the body as a thread with no `threadContext`, and only
then cast the vote. Comments first so a failure never leaves a vote standing with no reasons written
down; on failure it logs how many were posted so the reader knows what to look for on the site.

`REQUEST_CHANGES` maps to **`wait-for-author` (-5), not `reject` (-10)** - deliberately: "reject"
blocks completion outright, where GitHub's request-for-changes is a reviewer asking.

## What is unsupported on Azure DevOps

| Not supported | How it surfaces |
| --- | --- |
| **Pull requests from forks** | `RefusedException("Pull requests from forks are not supported on Azure DevOps yet.")` |
| **Server-side branch update** | `RefusedException("Azure DevOps has no server-side update-branch; rebase the branch locally and push.")` |
| **Line totals in the PR list** | `Additions`/`Deletions`/`ChangedFiles` left at `0`; both would be a call per row |
| **Check state in the PR list** | `StatusCheckRollup: null` -> `ChecksBucket == "none"` -> the dots hide |
| **"Last updated"** | the list carries no last-touched date; `UpdatedAt` is `creationDate`, so "most recently updated" really means "most recently opened" |
| **Stale-review marker** | `CommitId: null` - the marker never appears |
| **A ```suggestion``` block** | nothing host-specific; it posts as a plain code block, which Azure DevOps renders and nobody can apply |
| **The merge-queue drainer** | `HasMergeQueueWorkflowAsync` -> `false`; the queue is drained by whoever has the window open |

## PrCache - reading a pull request without the host

```csharp
public sealed record PrSnapshot(PrDetail Detail, string HeadSha, string BaseSha, DateTimeOffset TakenAt,
    IReadOnlyList<PostedComment>? Comments = null, IReadOnlyList<CheckRun>? Checks = null);
```

Only what the host alone knows. **The change itself is never in here**: its commits are in the object
database from the fetch that first opened the review, and the diff is read from them.

Layout: `$HOME/.cache/stampeded/prs/<sanitized repoKey>_pr<N>.json`, written indented. The `repoKey`
is `Path.GetFileName(RepoPath)` - the **folder name** - so two clones of different repositories in
identically-named folders share cache files.

`Load` returns `null` on `IOException` or `JsonException` and logs it - "an unreadable cache is a
cache miss, which is a working state". `Save` swallows `IOException` with a log line. **Neither is
ever fatal**: deleting the whole directory costs a reader nothing but the ability to open a review
offline.

### The offline path

1. Try `GetPrAsync` -> `FetchPrHeadAsync` -> `FetchBranchAsync` -> `GetMergeBaseAsync`.
2. On `ToolFailedException`, `OpenableSnapshotAsync` loads the snapshot **and rev-parses both its
   SHAs**. A snapshot whose head has been garbage-collected describes a review that cannot be built,
   so the original failure is reported instead - that is the honest answer.
3. On success: `Offline = true`, `OfflineSince = cached.TakenAt`.
4. The snapshot's checks go through `SetChecks` so the overview stops waiting for an answer that is
   not coming.
5. Offline, `LoadIssueUrlPrefixAsync` and `LoadReviewersAsync` are **not** called.
6. `KeepSnapshot` writes back **only when not offline**, so an offline session never overwrites a
   good snapshot with a degraded one.
7. An offline review refuses a verdict outright, naming the snapshot's age.

## CommentAnchor

```csharp
public sealed record CommentAnchor(string Path, bool OldSide, int Line, string LineText,
    IReadOnlyList<string> ContextBefore, IReadOnlyList<string> ContextAfter);
const int FuzzRange = 20;
```

A position that survives a force-push: the line's own text plus a small window, re-attached **by
content** rather than by number.

- **Drafts**: `Create(path, oldSide, line, fileLines, context = 2)` - two lines either side, taken
  from the blob on screen when the draft is written.
- **Posted comments**: `FromDiffHunk(path, oldSide, originalLine, diffHunk)`. GitHub drops a
  comment's line number once the diff has moved on but keeps the excerpt it was written against, and
  **that excerpt ends at the commented line**. So: filter the hunk to the side the comment is on,
  strip the marker column, and the last surviving line *is* the commented line. `ContextAfter` is
  therefore always empty for a posted comment.

`Reattach(fileLines)` returns a 1-based line or `null` (meaning **Outdated**):

1. Collect every line whose text equals `LineText`. None -> `null`.
2. Score each candidate by surviving context, take the best (ties broken by nearness). **Exact
   stage:** a full-window score returns immediately.
3. **Fuzzy stage:** otherwise the nearest text match within `FuzzRange` (20) lines of the original -
   text matches farther than that, once their context is gone, are not the same location.

`Approximate(fileLines)` is the best-effort answer when `Reattach` failed - **never null**. It slides
a ghost position over the file scoring only the surviving **non-blank** context lines (blank lines
match everywhere and would win by accident).

**Gate at submission time:** a draft is only posted when it has a current line **and** that line is
in `ChangedLines.CommentableOld/New` **and** the file is not generated - a generated file has no
counterpart in the pull request, so the host would reject the whole review over it.

## ReviewStateStore

On disk: `$LOCALAPPDATA/stampeded/reviews/<file>.json` (on Linux
`~/.local/share/stampeded/reviews`), written indented, **to `<path>.tmp` then `File.Move(...,
overwrite: true)`** - the file is rewritten in full on every toggled flag, and a write interrupted
halfway would read back as a review nobody ever started, drafts and all.

| Scope | File name |
| --- | --- |
| Pull request | `{repoKey}_pr{N}.json` |
| One commit | `{repoKey}_commit_{sha[..9]}.json` |
| Uncommitted work | `{repoKey}_worktree_{tipSha[..9]}.json` |
| Local `base..head` | `{repoKey}_local_{rangeKey}.json` |

A commit scope is keyed **by the commit**, because having read a file in one commit says nothing
about the next commit's change to it. The working-tree scope is keyed by the commit it sits on
rather than by content: the checkout changes with every save, and a file read there has been read
*for the tip it was written against*.

```csharp
public sealed record StoredComment(Guid Id, CommentAnchor Anchor, string Body, DateTimeOffset CreatedAt,
    long? InReplyTo = null);
```

`InReplyTo` is the REST id of the posted comment being answered - that makes it a reply into a
thread rather than a new comment on the same line, and replies survive their line moving.

### The head-move protocol - the subtle part

`OpenFile` has four branches:

1. **No file** -> write a fresh one **immediately**, before anything has been read. What the next
   pass needs from this one is the head it was opened at.
2. **`HeadSha` differs** - the push happened:
   - `Superseded = (oldHead, copy of Viewed)` - a one-shot signal, non-null only for the open that
     discovered the move.
   - `Viewed` is cleared; `PreviousHead`/`PreviousBase` take the outgoing values.
   - `PreviousMarkedHead = MarkedHead ?? PreviousMarkedHead`, likewise for `Submitted` - **a pass
     that neither ticked a file off nor submitted anything leaves the older marks standing**, which
     is what keeps merely *opening* a review from counting as having read it.
   - `Marked*`/`Submitted*` are reset, and the file is saved **now**: reopening before the next
     `SetViewed` must not re-read the old head from disk and report a second supersede.
3. **Same head, different base** - the target branch moved under the same work; record the new base.
4. Same head and base -> nothing.

`SetViewed`: ticking a file off is the act that makes this head a pass, so it stamps
`MarkedHead`/`MarkedBase`. **Unticking does not** - it says the reader wants to look again, not that
they never did. `RecordReviewSubmitted` stamps `SubmittedHead`/`SubmittedBase`.

A corrupt state file is logged and treated as a fresh start - but said out loud, "because everything
the reader recorded here is about to look like a first pass that never happened".

`ReviewStateFile.Depth` is **dead**: it carried a per-file review plan that no longer exists, and
the field stays only so older state files parse.

### ReReview

```csharp
public static IReadOnlyList<string> CarryOverViewed(
    IReadOnlyDictionary<string, bool> previousViewed, IReadOnlySet<string> touchedSinceLastPass)
    => previousViewed.Where(kv => kv.Value && !touchedSinceLastPass.Contains(kv.Key)).Select(kv => kv.Key).ToList();
```

Re-review is a different process, not a repeat: prior conclusions stay valid except where the new
push touched them.

## The rest of Review/

- **`ReviewVerdicts.Latest(reviews)`** - each reviewer's most recent review **that took a
  position**. A `COMMENT` takes none and leaves an earlier verdict in force; `DISMISSED` sets the
  entry to `null`. Ordered by name, because a list that reorders itself as people revisit a review
  reads as new activity. Note it iterates `OrderBy(r => r.SubmittedAt ?? MinValue)` - on Azure
  DevOps every `SubmittedAt` is null, harmless only because that host emits one entry per person.
- **`TriageEstimate`** prices a review by what the lines *are*: implementation 5 lines/min, tests 15,
  generated 50, a dependency file a flat 2 minutes, 75 minutes to a sitting. `Compute` overrides the
  filename guess with `file.IsGenerated` - output collected from a build is generated whatever it is
  called; the filename hints exist only to guess at generated code that was *committed*.
- **`TestPaths.IsTestPath`** is `path.Contains("test", OrdinalIgnoreCase)`. Deliberately crude, and
  it will call `src/Contest/...` a test.
- **`FolderOrder.ByFolder`** regroups a flat ordering by directory so it can be shown as a tree. The
  changed-file list puts a directory where its first file would have been; an order that leaves a
  directory and comes back to it ("tests first", "touched since the last pass first", "generated
  output last") is an order that tree cannot show, and the keyboard keys that walk the list would
  then walk it in an order the reader is not looking at.
- **`FixtureAssemblies`** is ILSpy-specific: sources under `ICSharpCode.Decompiler.Tests/TestCases/`,
  with variant sources (`Name.opt.roslyn.il`) collapsed onto the base fixture name by truncating at
  the first dot.
- **`IssueLinks.Autolink`** rewrites `#1234` as a markdown link. The regex puts the **skip
  alternatives first** so a `#123` inside fenced code, an inline span, an existing link, an autolink
  or a bare URL (whose fragment can look exactly like a reference) is consumed as part of that thing
  and never rewritten.
- **`ReviewAttribution`** appends the mark **once**, to the first thing a reader will meet: the first
  line comment if there is one (the file view, the thread and the mail notification all show those,
  and none of them shows the summary the comments were batched into), otherwise the summary. An
  approval with nothing written at all is left alone - the mark would be the entire review.

## MergeQueueService

### The idea

Clients never talk to each other and have no server. The queue lives on the one thing they all
reach: **a ref on the remote**, `refs/stampeded/merge-queue` - outside `refs/heads` and `refs/tags`
on purpose, so no branch or tag list shows it, no clone fetches it by default, and no branch
protection rule applies to it.

The ref points at a chain of commits with an **empty tree**, each carrying the whole queue JSON as
its message and each parented on the state it replaces. Writing is a plain `git push`, which git
refuses unless it fast-forwards - **that refusal is the compare-and-swap.** Because each state names
its predecessor, `git log` on the ref is the queue's own history.

### The document

```csharp
public sealed record MergeQueueEntry(int Pr, string Title, string HeadSha, string Method, string By,
    DateTimeOffset At, bool DeleteBranch = false);
public sealed record MergeQueueLock(string Holder, string Client, DateTimeOffset At, int Pr);
public sealed record MergeQueueDocument(int Version, IReadOnlyList<MergeQueueEntry> Entries, MergeQueueLock? Lock);
```

`HeadSha` is the revision that was cleared: a push after it makes the entry stale. `Method` and
`DeleteBranch` are the *enqueuer's* choice, carried so whichever client (or the drainer workflow)
drains the queue merges the way they meant. `Holder` is who to name in the UI; `Client` is a
per-process GUID prefix so **two Stampeded windows on one machine can still tell whose lock this
is**.

### Read and write

`ReadAsync`: `git ls-remote origin <ref>` - an empty listing is an **empty queue, not an error**.
Then `git fetch origin +<ref>:<ref>` (mirrored, not merged) and `git cat-file commit <sha>`. **A
document that does not parse throws** after logging - "a queue nobody can read is worse than an empty
one only if it is silently emptied."

`UpdateAsync(edit)` is the only writer. `edit` receives the current document and returns
`(replacement, subject)` or `null`. **It may be called more than once** - a push another client won
is not an error, it is the signal to re-apply. Up to `WriteAttempts = 5`.

```
git mktree                          # stdin empty and closed -> names AND stores the empty tree
git commit-tree <tree> [-p <parent>] -m "<subject>\n\n<json>"
git push origin <commit>:refs/stampeded/merge-queue     # no --force, no lease
```

**No force and no lease is the whole design**: a queue state that does not descend from the one on
the remote is exactly what must not be published, and git already refuses it.

`RaceLostAsync(expected)` decides whether a rejection was somebody else's write landing first by
**re-reading the ref** and comparing SHAs - reading git's refusal would mean matching on its wording,
and a push refused for want of access leaves the ref where we found it.

### One turn - `DriveOnceAsync`

```
read; empty -> "The queue is empty."
someone else's unexpired lock -> "#N is being merged by <holder>."
for each entry, in order:
    GetMergeStateAsync
      ToolFailedException      -> Pass(reason); continue      # one entry's problem, not the queue's
    state.State is MERGED/CLOSED -> RemoveAsync; return "was already <state>; dropped it."
    HeadRefOid != entry.HeadSha  -> Pass("pushed to since it was queued; queue it again")
    !state.CanMerge              -> Pass(state.Summary)
    !TryAcquireAsync             -> return "Another client took #N first."
    MergePrAsync -> drop the entry AND clear our lock in one write -> return "Merged #N (method)."
      ToolFailedException      -> ReleaseAsync; Pass(message); return "Merging #N failed: ..."
-> "Nothing in the queue can be merged right now (K waiting)."
```

Two decisions worth keeping:

- **Entries that cannot be merged are passed over, not left to block the queue.** A failing check at
  the front is one person's problem, not everybody's - and the reason is reported so the pane can
  say why.
- **An entry is dropped when its pull request is seen merged or closed, not when *this client*
  merged it.** That is what makes a client dying mid-merge harmless: its lock runs out, the next
  driver finds the pull request already merged, and drops the entry.

`BreakLockAsync` is safe because the lock was never what makes a merge exclusive - the host is; the
worst a broken lock can do is let a second client attempt a merge the host then refuses.

`WhyGoneAsync(pr)` reads `git log --format=%s -100 <ref>` and returns the first subject that mentions
`#pr` and is not an `enqueue `/`lock ` line. `null` when the history says nothing - an entry can also
vanish because somebody rewrote the ref, and inventing a reason would be worse. `Mentions` checks the
character after the token so `#14` never answers for `#142`.

`LeaseTime` is 5 minutes, marked `ponytail:`: a fixed lease against unsynchronised clocks, sound only
because a wrong steal is harmless here. A queue that waited for CI would hold the lock for as long as
CI takes and would need a real heartbeat.

**`refs/stampeded/*` triggers no `on: push` workflow** - GitHub Actions accepts only branches and
tags there. That is why the queue cannot be its own event and `repository_dispatch` exists.

## Gotchas

1. **GitHub's words are the model's words.** Adding a GitHub-shaped field to `MergeState` or
   `PrSummary` obliges `AzureDevOpsService` to synthesize it.
2. **`MergeState.Host` defaults to `"GitHub"`.** Not setting it makes every explanation lie.
3. **`gh pr checks` exits non-zero on failing or pending checks.** Anything routed through
   `ExternalTool.RunAsync` instead of the hand-rolled call would throw on a normal red build.
4. **A 404 from `gh api .../issues/N` is an answer.** `#141414` is a colour.
5. **The title memos cache the in-flight `Task`, not the result**, in a plain `Dictionary` - **UI
   thread only**. Calling from a background thread is a data race.
6. **The GraphQL walker uses hard `GetProperty`.** A schema change throws `KeyNotFoundException`,
   which nothing in the comment-loading path catches.
7. **`reviewThreads(first: 100)` / `comments(first: 50)` are unpaged.**
8. **`RefusedException` is not caught uniformly.** The PR list, the offline fallback and the queue
   driver all catch only `ToolFailedException` - see the refactor notes.
9. **Azure DevOps comment ids are packed decimal**, 6 digits for the comment. `PackId`/`SplitId` must
   stay each other's inverse.
10. **`PrCache` and `ReviewStateStore` key on the repository *folder name***, not the remote URL.
11. **The two stores use different base directories and different sanitizers.**
12. **`Superseded` is one-shot; `PreviousHead` persists.**
13. **Ticking a file off is what makes a head a pass.** Opening a review does not.
14. **The state file is rewritten in full on every flag toggle.**
15. **The merge-queue ref is pushed without `--force`, deliberately.** Adding a force or a lease
    anywhere in `PublishAsync` destroys the only mutual exclusion in the design.
16. **`MergeQueueEntry.Method` is a `gh` flag name** even on Azure DevOps, where `MergePrAsync`
    translates `merge` -> `noFastForward`.
17. **Azure DevOps `GetMergeMethodsAsync` reads the *default* branch's policy**, not the PR's target.
18. **`InvokeAsync` writes the request body to `Path.GetTempPath()`** - review prose transits the
    system temp directory in plaintext. Deleted in a `finally`, but not on a hard kill.
