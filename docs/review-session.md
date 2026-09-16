# The review session (orchestration layer)

`src/Stampeded/ReviewWorkspace.cs`, `ReviewScopes.cs`, `ReviewComments.cs`, `MainViewModel.cs`,
`Program.cs`. This is the layer that turns "a PR number" into "a window full of documents", and
it is where most cross-cutting behaviour of the app lives.

## Startup

`Program.Main` (`src/Stampeded/Program.cs:24`) does four things before any window exists, in this
order, and the order matters:

1. `OPENSSL_ENABLE_SHA1_SIGNATURES=1` is set **process-wide**, not per invocation. Child processes
   inherit it: `git`, `gh`, `dotnet`, and - the reason it cannot be per invocation - the MSBuild
   build hosts that `MSBuildWorkspace` spawns for itself.
2. `MSBuildLocator.RegisterDefaults()`. Must run before any Roslyn workspace assembly loads, or
   MSBuild resolves the wrong assemblies (see the `Microsoft.Build.Framework`
   `ExcludeAssets="runtime"` note in `Directory.Build.props`, guarding MSBL001).
3. Argument parsing: `--pr N` sets `Program.AutoOpenPr`; the first non-option argument that is not
   the value of `--pr` becomes `Program.RepoPath`. (The exclusion is a real bug fix: `--pr 4013
   /path/to/repo` used to take `4013` as the path.)
4. `Program.Host = PullRequestHosts.ForAsync(RepoPath)` - blocked on synchronously, deliberately:
   there is no dispatcher to deadlock against yet and the first window is built from the answer.

Then Avalonia starts. `BuildAvaloniaApp` maps Windows font family names (`Consolas`, `Segoe UI`, ...)
onto fontconfig aliases on non-Windows - third-party styles name those fonts outright and an
unresolvable family aborts the layout pass. On Windows the mapping must NOT be applied: there the
named fonts are real and the aliases are the unresolvable ones, and with them in place no window
ever appears.

`Program.RepoPath` and `Program.Host` are mutable statics: "Open Repository" changes both at
runtime. They are a property of the repository, so they sit together.

## MainViewModel

`MainViewModel` (`src/Stampeded/MainViewModel.cs`) is the window's view model, and it is small on
purpose - it owns no review logic, only what the menu bar needs to grey items correctly.

Construction order (`:133`) is load-bearing:

- `ZoomState.Set(Zoom)` hands the remembered zoom to the popup transform (nothing else sets it).
- `RecentRepos.Record(Program.RepoPath)` runs **before** `Recent` is snapshotted, because both
  views of the list (this menu and the start page) snapshot it during this constructor.
- `new ReviewWorkspace(Program.RepoPath, Program.Host)` -> `App.Workspace` (a static, which is how
  panes and views reach the session).
- `new StampededDockFactory(workspace)` -> `CreateLayout()` / `InitLayout()`, then
  `workspace.Factory` and `workspace.Documents` are wired back. The workspace cannot build its own
  layout; it is handed one.
- `workspace.OpenStart()` puts the start page in front.

`RefreshReviewState()` is the single place that reads review state into the menu's observable
flags, hooked to `ReviewChanged` and `Scopes.Changed`. The state of the *tab in front* is
deliberately absent: it has no event, so the menu reads it when it opens.

## ReviewWorkspace: what it is

One instance per repository, created by `MainViewModel` and abandoned (via `Shutdown()`) when the
app switches repositories. It owns:

| Field | What it is |
| --- | --- |
| `RepoPath`, `Git`, `Blobs`, `Worktrees` | git access for this clone |
| `Host` / `HostName` | the pull-request host, decided once from origin's URL |
| `MergeQueue` | the shared merge queue (note: constructs a **second** `GitService`) |
| `Store` | `ReviewStateStore` - viewed flags, drafts, pass heads |
| `Busy` | the busy tracker the status bar shows |
| `Comments` / `Scopes` | lazily created collaborators, each handed `this` |
| `Factory` / `Documents` | set by `MainViewModel` once the dock layout exists |

The review itself is the quadruple `BaseSha` / `HeadSha` / `Files` / `changed`, and those four
must move together - `SetScopeContent` (`:1327`) exists precisely to make that atomic, and is
`internal` so only `ReviewScopes` can call it.

**`BaseSha` is not always a commit.** In the since-last-pass scope it is a *tree* built for that
scope. Reading a blob out of it works; blame, a worktree, `rev-parse ^` do not - git answers
"Non commit"/"invalid reference". Anything needing history or a checkout must first ask
`Scopes.InSinceLastPass`. `EnsureBaseWorktreeAsync` (`:655`) is the model for this: it refuses
with a sentence naming the reason rather than letting git refuse four callers deep.

## Opening a review

Two entry points, and they are near-parallel:

- `OpenPrAsync(int number)` (`:452`)
- `OpenLocalRangeAsync(baseRef, headRef, prNumber = null)` (`:369`)

Both follow the same nine-step shape:

1. Cancel the previous session (`sessionCts`), take a new token. Everything background in a review
   is tied to this token.
2. Resolve `headSha` / `baseSha`. For a PR: `Host.GetPrAsync`, `Git.FetchPrHeadAsync` (via
   `Host.PrHeadRefspecAsync`), `Git.FetchBranchAsync(detail.BaseRefName)`, then
   `GetMergeBaseAsync`. The base is **the PR's own target**, not the repository default branch -
   a branch targeting a release branch is not a diff against master.
3. `Git.DiffAsync(base, head)`, or `DiffWorkingTreeAsync` when a dirty checkout holds the branch
   (`FindDirtyCheckoutAsync`, `:1249`). `UncommittedFileCount` is the difference.
4. Reset session state: `Scopes.Reset()`, `Reviewers = null`, `Offline`, `snapshot`, `PrHeadSha`.
5. Set the review quadruple, fire `ReviewReset`.
6. `Store.Open` / `Store.OpenLocal` - keyed by repo name plus PR number or the *range text the
   user typed* (`ReviewScopes.LocalRangeKey` must key it identically on scope exit, or the state
   file is orphaned).
7. `ApplyReReviewCarryOverAsync`, `PinReviewHeadsAsync`, `ComputeChurnAsync`.
8. `history.Clear()`, close documents, fire `ReviewChanged`, `OpenOverview()`, `CloseStartPage()`.
9. Fire off the background loads, each `.HandleExceptions()`: scope-then-semantics, generated
   sources, draft reattachment, posted comments, reviewers, issue-URL prefix.

The overview opens rather than every file: files open one tab at a time as the Explorer list is
walked.

### Offline

`OpenPrAsync` catches `ToolFailedException` around the host calls and falls back to
`OpenableSnapshotAsync` (`:538`) - the `PrCache` snapshot, but **only if both its SHAs still
rev-parse**. A snapshot whose head was garbage-collected describes a review that cannot be built,
and rethrowing the original failure is then the honest answer. Offline: `Offline = true`,
`OfflineSince` set, cached checks pushed through `SetChecks` so the overview stops waiting, and
issue-prefix/reviewer loads are skipped entirely rather than asked and failed.

`KeepSnapshot` writes through to `PrCache` only while online, and is updated as parts arrive
(`SetChecks`, `KeepComments`).

### Semantics load order

`LoadScopeThenSemanticsAsync` (`:568`): **scope first, then workspaces**. The scope decides what
the review *is* (seconds); the workspaces take as long as a solution load. Restoring the scope
after them would rearrange a review already being read. Because entering a scope overlays text
onto the workspaces and this load replaces them, the overlay is re-applied at the end.

`LoadSemanticsAsync` (`:576`) disposes the old providers, creates the head worktree, loads C#
(in process, or over LSP when `STAMPEDED_SEMANTICS=lsp`), starts other-language servers, computes
the change map, prunes the worktree cache to `KeptWorktrees = 6`.

The base-side C# workspace is **not** a second solution load: `baseSemantics.LoadFrom(headSemantics,
replaced, removed, added)` reuses the head compilation with the review's files reading as they did
before, from the object database (`BaseSideTextsAsync`, `:624`). A second checkout+restore+design-
time build would spend minutes arriving at the same answers.

Python is different: a language server holds one text per file, so the base side must be a second
process on a checkout of the base revision (`TryStartPythonAsync`, `:2106`).

### Provider lookup

- `SemanticsFor(bool oldSide)` - the primary (C#) pair.
- `SemanticsFor(bool oldSide, string relPath)` (`:1948`) - by file extension, through the
  `languages` list, falling back to the C# pair only for `.cs`/`.csx`. Everything else gets
  `null`, and `NoProviderMessage` (`:1977`) says which nothing it is: "nothing reads .json files"
  vs "nothing reads the base side of .py files in this review". Without that, a `.md` handed to
  Roslyn produces the same silence as "still loading".

## Scopes

`ReviewScopes` owns *which part* of the review is on screen; the workspace owns what the review
is. Three states, one way out ("Whole change"):

- **Whole change** - `Commit is null && !InSinceLastPass`.
- **Commit by commit** - `Commit`/`Series`/`CommitIndex`. The series is oldest-first (the order it
  was written), with `CommitInfo.WorkingTree` appended when the checkout is dirty. Each step
  re-keys the state store (`Store.OpenCommitScope`) so viewed flags are per commit. The parent
  comes from `commit.FirstParent`, carried by the log, rather than a `rev-parse` per step.
- **Since last pass** - diffs against `SinceLastPassBase`, a **tree** produced by
  `Git.ReplayTreeAsync(base, previousHead, previousBase)`: everything the reader already read,
  replayed onto the current base. A tree and not a commit because after a rebase no commit's diff
  to the head is the author's own edits. Unlike the commit scope, it shares the review's state
  file: its head *is* the review's head, so a file read here has genuinely been read.

Refusals are properties, not silent disabled controls: `CommitScopeRefusal` / `SinceLastPassRefusal`
return the same sentence the enter method would post, so the tooltip and the outcome cannot drift.

`PassBaselineKind` picks what "last pass" means: `MarkedViewed` (default - opening a review is not
reading it), `SubmittedReview`, `Opened`. `ReviewWorkspace.ReadPassBaselinesAsync` (`:989`) only
offers those whose commit is still in the repository.

`ScopeKey` (`"commit:<sha>"`, `"pass"`, or null) is recorded into every navigation history entry,
and `RestoreScopeAsync` puts the scope back before navigating - a line recorded in one commit's
diff is not the same line in the whole change.

`fullRange` remembers the range to return to; `Reset()` clears everything scope-owned because a
freshly opened review is the whole change by definition.

## Comments

`ReviewComments` holds drafts (local, in the state store) and posted comments (from the host),
plus the placement of both in the code as it now stands.

Placement is a three-step fallback, used identically for drafts (`ReattachDraftsAsync`, `:148`)
and posted comments (`LocateAsync`, `:261`):

1. `CommentAnchor.Reattach(blobLines)` - find the line by content. Exact.
2. `MemberRelocation.Locate(oldText, oldLine, newText, lineText)` - read the member the comment was
   written in out of the revision it was written against, and find that member now. Reported as
   `MovedTo` ("moved with `Foo.Bar`", or "the exact line is gone; placed in `Foo.Bar`").
   C# only, and only when that old revision is still in the clone.
3. `CommentAnchor.Approximate(blobLines)` - best guess from surviving context, flagged
   `IsApproximate`.

Blob reads are memoised per `(rev, path)` in `blobs` for the whole pass; a ten-comment thread on
one file was ten `git show` calls.

`SubmitCheckedAsync` (`:386`) is the single gate every submission goes through - both the Comments
pane and the review document - so a verdict cannot be refused in one and slip through the other.
It refuses, in order: offline; `LocalHead` (the branch is ahead of what the host has, so line
comments would land on lines the host does not have - replies are exempt, they name a thread);
`APPROVE` with an unread series; `APPROVE` blocked by the guide's `ApprovalGate`;
`APPROVE`/`REQUEST_CHANGES` on your own PR where the host refuses it (`Host.AcceptsOwnApproval`).
Then it **leaves the scope** rather than refusing, because drafts are matched against the files in
scope, and reports what it did in the result line.

`SubmitAsync` (`:459`) splits drafts into line comments and replies. A reply goes as its own
request (it survives its line moving); a line comment is dropped ("kept local") when its line is
not in `Changed.CommentableNew/Old`, or when the file is generated - the host would reject the
whole review over it. An all-replies pass submits no review at all, and the first reply carries
the attribution mark the review body would have (`ReviewAttribution.AttributedReply`).

## Navigation history

`NavigationHistory<NavEntry>`; `NavEntry` is `(DockableId, BlobLine, OldSide, Scope)`.

`RecordCurrentPosition()` (`:2493`) **rewrites** the current entry rather than pushing when it is
already the current document in the same scope: an entry recorded at open says line 1, and coming
back to line 1 of a half-read file is not coming back.

`NavigateToEntryAsync` (`:2526`) restores the scope first, then handles the id prefixes. The
prefix vocabulary is the app's document-identity scheme and appears in several places:

| Prefix | Document |
| --- | --- |
| `diff:<path>` | a file of the review (unified **or** side-by-side - same id, one tab) |
| `src:<path>` / `srcbase:<path>` | a file outside the change, head / base side |
| `source:<abs path>` | a definition outside the tree (a package), read-only |
| `decomp:<reflection name>` | a decompiled type; only revisited while still open |
| `hist:<sha>:<path>` | one commit's change to one file |
| `show:<sha>` / `interdiff:<sha>` | a whole patch as text |
| `overview`, `start`, `review` | the three singleton documents |

## Events

`ReviewWorkspace` exposes 18 events. Two are easy to confuse and the distinction is documented in
the source:

- `ReviewChanged` - fires whenever anything about the current review changed, **including while
  one review is being read** (generated sources arriving, the issue prefix arriving).
- `ReviewReset` - a *different* review is in front, or none. This is the signal for a pane to
  throw away derived state (references, call graph, file history). A pane that emptied itself on
  `ReviewChanged` would go blank under the reader's hands.
