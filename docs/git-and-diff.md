# Git, diff and infrastructure

`src/Stampeded.Core/{Git,Diff,Infra}` - the part of the tool that turns a repository into
something reviewable. No Avalonia anywhere in it.

## The shape of the layer

```
Infra/   ExternalTool, CliLog, CachePath, GuessFileType, LogFileRefs, SolutionTarget
   ^                          ^
   |                          |
Git/     GitService -- GitDiffParser --+
         GitBlobReader                 |
         GitLogParser, GitBlameParser  |
         WorktreeManager, BranchSync   |
                                       v
Diff/    DiffModel (FileDiff/DiffHunk/PatchLine)
         DiffDocumentModel + DiffDocumentBuilder  (uses DiffSlider, DiffLib)
         PatchDocumentBuilder, ChangedLines, ContextGaps, DiffFolding
```

`Git/` depends on `Diff/` (for `FileDiff`) and on `Infra/`. `Diff/` depends on nothing but
`Semantics.MemberFoldRegion` (one type, in `DiffFolding`) and the external `DiffLib` package.

### The one architectural fact that explains everything else

**There are two entirely separate diff representations, produced by different machinery, and they
do not agree line for line on purpose.**

1. **`FileDiff` / `DiffHunk` / `PatchLine`** - parsed out of `git diff -U3 --find-renames` by
   `GitDiffParser`. This is *git's* opinion. It drives the file list, the change kinds, the rename
   detection, and - critically - `ChangedLines`, which decides where a review comment may be
   anchored. It has to be git's opinion, because GitHub and Azure DevOps compute their comment
   anchors from the same diff; a comment offered on a line the host's diff never printed would fail
   to post.

2. **`DiffDocumentModel`** - built by `DiffDocumentBuilder.Build` from the *whole* old blob and the
   *whole* new blob, re-diffed in process with DiffLib and post-processed by `DiffSlider`. This is
   what the reader looks at. It contains every line of both files, so the reader can scroll out of a
   hunk into untouched code, ask for a definition, blame, and fold members. Context hiding is layered
   on top by `ContextGaps` rather than baked into the text.

The blobs for (2) come from `GitBlobReader` (`ReviewWorkspace.cs:634, 777, 1286`), not a checkout.

## Infra

### ExternalTool

The single door out of the process for every CLI (`git`, `gh`, `az`, `dotnet`, `code`, `xdg-open`).

```csharp
public sealed class ToolFailedException(string tool, int exitCode, string stdErr) : Exception
public sealed class RefusedException(string message) : Exception

public static Task<string> RunAsync(string exe, IReadOnlyList<string> args, string workingDir,
    CancellationToken ct = default, IReadOnlyDictionary<string, string>? env = null,
    IReadOnlyList<int>? okExitCodes = null);
public static string Explain(ToolFailedException failure);
public static string FailureReason(string stdErr, string stdOut);
```

`RunAsync` returns **stdout only**. A non-zero exit throws `ToolFailedException` carrying the whole
of stderr, unless the code is listed in `okExitCodes`.

**The two exception types are not the same thing.** `ToolFailedException` means *a CLI said no* and
has an exit code and stderr. `RefusedException` means *this tool said no*, before or instead of
running anything - only two producers in this layer: `GitService.RemoveWorktreeAsync` (`:514`) and
`GitService.RebaseBranchAsync` (`:594`). Callers should treat them identically: "it did not happen,
and the message says why".

Non-obvious behaviour:

- **`Win32Exception` is converted, not propagated** (`:56`). A tool that is not installed throws it
  from `Process.Start`, which no caller catches; it would escape and leave a pane spinning forever.
  It becomes `ToolFailedException(exe, -1, "...not installed, or not on PATH.")`. **Exit code `-1`
  is the sentinel for "never started".**
- **MSBuildLocator variables are stripped from *every* child**, git included (`:28`, `:49`).
  `MSBuildLocator.RegisterDefaults()` pins `MSBUILD_EXE_PATH` / `MSBuildSDKsPath` /
  `MSBuildExtensionsPath` process-wide to the SDK matching the *host* runtime. A child `dotnet`
  whose `global.json` resolves a different SDK then gets foreign targets forced on it and fails
  restore with no output at all.
- **Every invocation is logged** (`:65`): `argsText -> exit N (M ms)`, with the failure reason
  appended when it failed. Args truncated at 160 characters.
- `FailureReason` takes the first non-blank line of stderr, falling back to stdout (not every tool
  reports failure on stderr - `gh` in particular), capped at 200 chars.
- **Cancellation kills the process tree** - CliWrap's behaviour, relied on rather than implemented.

### CliLog

```csharp
public static Action<string>? Sink { get; set; }
public static void Write(string category, string message);
```

Line format `HH:mm:ss.fff [category] message`. `Backlog = 2000` lines are kept, and setting `Sink`
**replays the whole history into the new sink, inside the lock** (`:26`). Two reasons: the window is
built *after* the workspace it shows (and on Windows there is no console behind it), and switching
repositories builds a new Log pane. The replay is inside the lock so a concurrent `Write` queues
behind it rather than interleaving.

**Gotcha:** writers are on arbitrary threads. The sink installed by the UI must marshal to the UI
thread itself; `CliLog` does not.

### CachePath

`$XDG_CACHE_HOME/stampeded/<kind>`, falling back to `LocalApplicationData` on Windows and `~/.cache`
elsewhere. `SpecialFolder` is deliberately not used for the non-Windows case: it has no reliable
cache mapping and **can resolve to an empty string**, which would silently turn the path relative.
Known kinds: `worktrees`, `prs`, `python-lsp`.

### GuessFileType

```csharp
public enum FileType { Text, Xml, Json }
public static FileType DetectTextType(string text);
```

Carries an ILSpy/SharpDevelop MIT header - the XML half is lifted from
`ICSharpCode.ILSpyX/Util/GuessFileType.cs`. It exists because `.props`, `.targets`, `.axaml`,
`.slnx` and `.resx` are XML that no TextMate or xshd definition claims by extension, so the file is
highlighted by content instead.

**The one real divergence from ILSpy:** ILSpy stops at `MoveToContent()` because it already knows
the blob is a resource. Here the question is asked of any file whose extension said nothing - and
`"<T>(T value) => value"` opens with something `XmlTextReader` will happily call an element. So this
reads **to the end of the document** (`:77`); whether the whole thing closes is what tells markup
from code that merely starts with a bracket.

`XmlResolver = null` and `DtdProcessing.Ignore`: a DTD reference in a file *under review* must never
be followed. JSON requires a leading `{` or `[` (a bare number or string is valid JSON and is also
the first line of most text files) and allows comments and trailing commas.

### LogFileRefs

Finds `file:line` references inside a log line so the Log pane can make them clickable. Three forms,
because three families of tool write into this log: `Foo.cs(12,5)` (MSBuild), `Foo.cs:12` (git, gcc,
this tool), `Foo.cs:line 12` (.NET stack traces).

Gotchas encoded in the regex: the extension must start with a **letter**, or `1.5:30` is a file at
line 30; no colon is allowed inside a path, because the colon is the separator in two of the three
forms; URLs are rejected *after* matching by looking at the characters before the path (`:36`) - a
preceding `:` or `//` means `host:port` or a URL path. `Start`/`Length` span the **whole match**,
which is the clickable span.

### SolutionTarget

```csharp
public static string? ForRoot(string root, string? chosen = null);
public static IReadOnlyList<string> Candidates(string root);
public static string? ForSemantics(string root, string? chosen = null);
```

`ForRoot`, in order: root missing -> `null`; `chosen` wins *but only if the checkout still has it*
(a review worktree of an older revision may not); off Windows a `*.slnf` whose name contains `xplat`
wins (a full solution usually holds net472 add-ins and Windows-only test hosts, and the filter is
the repository's own statement of what builds elsewhere); otherwise the **largest** `*.sln`, else
the largest `*.slnx` - size as a proxy for "the product's own", since an installer or extension
solution holds a project or two; `null` means "let dotnet work it out", correct when there is
exactly one.

It exists because `dotnet` errors with MSB1011 rather than guessing, and because the tests pane and
the generated-sources build were picking separately.

**`ForSemantics` is deliberately different.** Roslyn opens a *solution*, not a filter, so when
`ForRoot` answers a `.slnf` this reads the filter's `solution.path` and returns that (`:69`). The
path in a `.slnf` is written with **Windows separators even in repositories that never see
Windows**, so it is rewritten to `Path.DirectorySeparatorChar`.

## Git

### BranchSync

```csharp
public enum BranchSyncState { InSync, Ahead, Behind, Diverged, Unfetched }
public sealed record BranchSync(BranchSyncState State, int Ahead, int Behind)
```

`Unfetched` is the important state: the heads differ but the PR head is **not in the local object
database**, so by how much cannot be said without fetching. `GitService.GetSyncStateAsync` cannot
produce it itself (it returns `null`); the caller substitutes `BranchSync.Unfetched`. The
`Ahead`/`Behind` convention matches `git rev-list --left-right --count local...remote`: left =
local-only = ahead.

### GitBlameParser

Parses `git blame --porcelain`:

```
<40-hex sha> <origLine> <finalLine> [<groupLines>]
author Jane Doe
author-time 1699999999
summary Fix the thing
\t<the actual source line>
<40-hex sha> <origLine> <finalLine>        <- same commit again: NO headers this time
\t<the next source line>
```

The header block appears **only on a commit's first occurrence**. So the parser keeps a
`Dictionary<string, CommitInfo>` cache (`:18`, `:44`) and re-points `current` at the cached entry.
Emission is driven by the **content line** (`line.StartsWith('\t')`, `:25`): a tab-prefixed line is
the only unambiguous marker, because header values can be anything.

**Gotcha:** `int.Parse`/`long.Parse` are unguarded. Malformed porcelain throws rather than degrading.

### GitLogParser

```csharp
public sealed record CommitInfo(string Sha, string ShortSha, string Author, string Date,
                                string Subject, string Body = "", string Parents = "")
public sealed record BranchInfo(string Name, string Sha, string Date, string Subject);
```

**`CommitInfo.WorkingTree`** is the pseudo-commit: `new("", "uncommitted", "", "", "the work in your
checkout, not committed yet")`. An empty `Sha` *is* the marker (`IsWorkingTree`). A review of a
checkout with uncommitted work appends this to the commit series so a reader stepping through the
change reaches the part nobody else can see yet. **Anything that does `sha[..9]` or `git show sha`
on a `CommitInfo` must check `IsWorkingTree` first.**

`Parents` is carried on the record rather than looked up, because asking git for a commit's parents
one at a time is a process each and reading a series asks for every one of them.

**The log record format** (`GitService.cs:359`):

```
--format=%H%x09%h%x09%an%x09%ad%x09%P%x09%s%n%b%x00
```

Two ordering decisions, both load-bearing:

- **The body needs a terminator no commit message can contain** - hence NUL - and it comes after a
  *newline* rather than a tab so a subject containing tabs stays whole.
- **The subject (`%s`) is last on the header line**, because it is the one field that can hold a
  tab. `Split('\t', 6)` therefore splits only the five separators before it. Anything added after
  `%s` would be cut out of a subject containing a tab.

`ParseShortStat` reads `git log --format=%H --shortstat A..B`. **A commit that changed nothing prints
no summary and is absent from the result** - "no lines" is expressed by absence, not by a `(0,0)`
entry.

`ParseNameStatus` takes `parts[0][0]` as the status char and **`parts[^1]` as the path** - renames
and copies (`R100`, `C75`) carry old *and* new path, and the last one is the current one.

`ParseBranches` serves two callers: `for-each-ref refs/heads` output **and** `git stash list
--format=%gd%x09%H%x09%cs%x09%gs`, deliberately shaped the same so both go through one parser.

### GitDiffParser

Parses `git diff -U3 --find-renames` unified output into `FileDiff`s.

The `diff --git a/<old> b/<new>` line is **deliberately not used for paths** (`:29`). It is
authoritative only when the two paths are equal, and paths containing spaces cannot be split out of
it without heuristics. Instead: renames from the explicit `rename from ` / `rename to ` lines,
adds/deletes from `new file mode` / `deleted file mode`, paths from `--- ` / `+++ ` with `/dev/null`
filtered out, and **binary paths from the `Binary files ... and ... differ` line only**, because git
emits no `---`/`+++` for a binary add or delete (`:70`).

**`ParseHunk` - content is bounded by the header's counts.** The body loop (`:120`) runs **while
`oldRemaining > 0 || newRemaining > 0`**, never by sniffing for the next `@@` or `diff --git`. That
is what keeps trailing blank lines and any `+`/`-`-looking content unambiguous - a removed line
whose text begins with `-` would otherwise be indistinguishable from a header.

- `\ No newline at end of file` is skipped as metadata (`:124`).
- A **completely empty line is treated as an empty context line** (`:140`): git emits a lone space
  for an empty context line, and some transports strip trailing whitespace.
- `ParseRange` handles the `,len`-omitted form, which means length 1.

**The `i--` dance:** `ParseFile` iterates with a `for` loop; `ParseHunk` takes `ref i` and leaves it
**one past** its last consumed line, so `ParseFile` does `i--` at `:87` to cancel the increment. The
two are coupled; easy to break.

**Known limit: combined diffs (`@@@ ... @@@`, merge commits) are not handled.**
`GitService.DiffAsync` never asks for one and `git show` of a merge goes to `PatchDocumentBuilder`
instead, but anything routing combined output here would find the wrong position.

### GitBlobReader

```csharp
public sealed class GitBlobReader : IDisposable
public Task<string?> ReadAsync(string revision, string relativePath, CancellationToken ct = default);
```

One `git cat-file --batch` per repository, kept alive, asked for one blob at a time. A review needs
the text of a few dozen files at a revision nobody has checked out. Every `.cs` file of a mid-sized
repository comes back through one batch in well under a tenth of a second; a checkout to serve the
same reads takes hundreds of milliseconds *and* a copy of the tree, and a process per file costs
more than reading them all does. **The trade is the point of the thing**, so the reader is owned by
the review that started it (`ReviewWorkspace.Blobs`, disposed at `ReviewWorkspace.cs:1359`). Do not
generalise this into "the way we run git".

The batch protocol: request `<revision>:<path>\n` on stdin; response `<oid> <type> <size>\n`, then
exactly `size` bytes, then **one newline of git's own**. The header shares a stream with blob
content, which is read by length and may hold anything at all. So `ReadLineAsync` (`:108`) cannot use
a `StreamReader`, which would buffer past the header and eat the start of the blob - it reads single
bytes until `\n`, from `git.StandardOutput.BaseStream`.

Anything that is not `<oid> blob <size>` - `missing`, `ambiguous` - means the object database has
nothing to read, and `ReadAsync` returns `null`. **`null` is an answer, not a failure**: a file the
change adds is absent from the base, and asking is how that is discovered.

Gotchas:

- **Serialized by a `SemaphoreSlim(1,1)`** - the protocol is a single request/response stream.
- **A dead reader restarts silently** (`:67`): `IOException`/`InvalidOperationException` -> log,
  `Stop()`, return `null`. So the *next* call works, but **this** call returns `null`,
  indistinguishable from "the revision does not have that file". If you are debugging a
  mysteriously-empty base side, check the log for `blob reader restarting`.
- **UTF-8 only** (`:65`). Binary blobs come back mangled; `FileDiff.IsBinary` is the guard.
- `size` is parsed as `int` - a blob over 2 GB fails the header parse and reads as `null`.
- `CreateNoWindow = true` (`:96`) is not cosmetic on Windows: a console child of a windowless
  desktop process gets its own console window, and this one **outlives the review**.

### WorktreeManager

Layout: `<CachePath.For("worktrees")>/<repo directory name>/<sha[..9]>`.

```
git worktree prune                       # a stale registration for a deleted dir blocks re-adding
git worktree add --detach <dir> <sha>
```

`--detach` is the invariant: **review worktrees never hold a branch**, so they can never collide
with the user's checkout or block a branch operation.

The reuse path (`:64`) checks `Directory.Exists(dir) && File.Exists(dir/.git)` - a linked worktree
has `.git` as a *file*, so this both proves it exists and proves it is still a worktree. It then
**touches the directory's last-write time**: reuse counts as use, so what the LRU keeps is what a
reader comes back to, not what happened to be built in most recently.

`PruneToRecentAsync`: pinned SHAs survive unconditionally; of the rest, the `recent` most recently
written survive. A worktree is a copy of the whole tree **plus whatever building it leaves behind**,
and one is made for every revision ever reviewed - left alone these are the largest thing this tool
puts on a disk.

`LinkSubmodulesFromSource`: `git worktree add` leaves submodules as **empty stubs**, so a test run in
a worktree cannot find its fixtures (the motivating case is ILSpy's `ILSpy-tests` submodule with its
offline nuget cache). `.gitmodules` is scanned with a crude `path =` line parse and each submodule
directory replaced with a **symlink to the source clone's checkout**, only when the source is
populated and the target is not. Failures are logged, never thrown. The parse is line-based, not
INI-section-aware.

### GitService

The class doc states the invariant:

> Reads never touch the user's working tree or index: they come from the object database (fetch,
> merge-base, diff, show) or, for a review of uncommitted work, from a checkout's files. The
> operations that write (branch creation, rebase) touch refs only, running any checkout they need in
> a throwaway worktree - the one exception being a rebase of a branch that a checkout already has.

So: **reviewing cannot disturb what the user has checked out; only an explicit rebase can.**

One private helper carries most calls; note the **cancellation token comes first** so `args` can be
`params`:

```csharp
Task<string> RunAsync(CancellationToken ct, params string[] args)
    => ExternalTool.RunAsync("git", args, repoPath, ct);
```

#### Result types declared in the same file

| Type | Meaning |
| --- | --- |
| `WorktreeCheckout(Path, Branch?)` | a checkout and the branch it holds; `null` = detached |
| `PullResult(PullOutcome, Sha)` | `Created`, `FastForwarded`, `AlreadyUpToDate`, `Diverged` |
| `PushResult(PushOutcome, Sha)` | `Created`, `Pushed`, `ForcePushed`, `AlreadyUpToDate` |
| `BranchDeletion(Sha, RemovedWorktree?)` | the commit the branch pointed at (the recovery point) |
| `RebaseResult(Before, Checkout?, RebaseOutcome, WorkingDirectory)` | see below |
| `InProgressOperation(Kind, WorkingDirectory, Branch?, IsScratch, Unmerged)` | see below |

**`RebaseResult.RecoveryCommand(branch)` - why two different commands:**

```csharp
Checkout is null
    ? $"git branch -f {branch} {Before[..9]}"
    : $"git -C {Checkout} reset --hard {Before[..9]}"
```

A branch no checkout holds is moved with `git branch -f`, which git **refuses** for a checked-out
branch. That one is recovered with `reset --hard` *in the checkout*, so its working tree follows the
ref back. `Checkout` being non-null is precisely the signal that the rebase ran in a real checkout.

**`InProgressOperation` - the decision table:**

- `CanResolve => Unmerged > 0 && Kind is not Bisect`
- `CanContinue => Unmerged == 0 && Kind is not Bisect` - **git refuses to continue with conflicts
  present, and offering a button git will refuse is how the merge tool came to look optional.**
- `CanSkip => Kind is Rebase or CherryPick or Revert` - a merge has one commit to make.

#### Probing and rev resolution

| Method | Command | Why this command |
| --- | --- | --- |
| `IsRepositoryAsync` | `rev-parse --is-inside-work-tree` | exit code is the answer |
| `RevParseAsync` / `TryRevParseAsync` | `rev-parse --verify <ref>` | throws / `null` |
| `HasCommitAsync` | `rev-parse --verify <ref>^{commit}` | **`rev-parse --verify` answers a full SHA with itself whether or not the object is there.** Only asking for the *commit it names* actually reads the database. |
| `IsAncestorAsync(a,d)` | `rev-list --count d..a` == "0" | **not** `merge-base --is-ancestor`, whose answer is its exit code - which this tool runner reports as a *failed command with a log line to match*, so a normal "no" would look like an error in the Log pane |
| `ReplayTreeAsync` | `merge-tree --write-tree [--merge-base=X] onto head` | see below |

`ReplayTreeAsync` computes **the tree a rebase would produce, entirely in the object database** - no
worktree, index or ref touched. `mergeBase` is passed explicitly for the case where the branch was
rebased somewhere else entirely. **Exit code 1 means the replay conflicted**, and it is converted to
`null`: merge-tree still prints a tree then, but one with conflict markers in it, and there is no
honest way to show that as the author's code. Used by `ReviewScopes.cs:567`.

#### Ref pinning and fetching

`PinReviewHeadsAsync(key, head, previousHead)` -> `update-ref refs/stampeded/review/<key>/head` and
`.../prev`. Why a ref of the tool's own is required: the PR head ref is force-updated by every
fetch, `refs/stampeded/pr/N` has no reflog, and a rewritten branch's old tip is referenced by nothing
at all. Without this, **the commit the reader compared against last time is prunable** and the
re-review diff evaporates.

`FetchPrHeadAsync(refspec, number)` takes its refspec **from the host** - GitHub advertises every PR
head as a ref, Azure DevOps does not and the source branch is fetched instead.

#### Diff reading

```
DiffAsync(baseRev, headRev)              -> git diff -U3 --find-renames <base> <head>
DiffWorkingTreeAsync(worktreePath, base) -> git diff -U3 --find-renames <base>   (cwd = worktree)
ShowFileAsync(rev, path)                 -> git show <rev>:<path>
DiffPatchAsync(base, head)               -> git diff <base> <head>        (raw patch text)
DiffNameStatusAsync(a, b)                -> git diff --name-status --find-renames a b
ListFilesAsync(rev)                      -> git ls-tree -r --name-only -z <rev>
```

`DiffWorkingTreeAsync` compares against the **working tree**, so it reports staged and unstaged
alike. Untracked files are not in it - git does not track them and neither does a review. It sorts
explicitly because git's working-tree diff order differs from its commit-diff order.

`ListFilesAsync` uses `-z` so paths with newlines survive, and reads from the object database rather
than a checkout - it is the *revision's* list, and it costs nothing when no checkout exists yet.

#### Worktree enumeration and status

`ListWorktreesAsync` parses `git worktree list --porcelain`. Detached checkouts have no `branch`
line, so `Branch == null`.

`IsDirtyAsync` -> `git status --porcelain --untracked-files=no`. **Untracked files deliberately do
not count**: they are not part of the change under review, and a checkout holding nothing but build
output is not a review step.

`FindCheckoutAsync(branch)` encodes the invariant: *a branch can be in only one checkout.*

#### Branch listing and merge analysis

**The two-question merge test.** `git branch --merged` is an ancestry test: cheap, one call for
every branch, and it answers "is this in there as it stands". A **rebase-merged branch is not among
them** - its commits were replayed and none of the originals survives. That is what
`IsMergedByPatchAsync` is for: `git cherry` marks a commit `-` when upstream has one with the same
patch id and `+` when it does not, so the branch is in when **nothing is marked `+`**. Documented
edge cases: a branch with no commits of its own answers true (correct - nothing left to merge); a
**squash-merged** branch of more than one commit answers **false**, because its commits were combined
into one whose patch matches none of them.

**`%(ahead-behind:...)` requires git 2.41+.** It was chosen because git can answer for all branches
at once; asked branch by branch it was one process each.

**`ListStashesAsync` reuses `ParseBranches`** by shaping `git stash list` output into the same four
tab-separated fields. The payoff (`:550`): a stash's own commit holds the stashed working tree and
its **first parent is the commit it was taken on**, so `sha^..sha` is exactly what `git stash show`
reports - and a stash therefore reviews as an ordinary local range with no special case anywhere
above.

#### Branch mutation

**`DeleteBranchAsync`** - three decisions:

1. Returns the commit the branch pointed at, because that is what it takes to offer the branch back.
2. **The worktree holding the branch is removed first** - git refuses to delete a branch some
   checkout has.
3. **`-D`, not `-d`, and that is not a shortcut.** `git branch -d` tests the branch against its
   *upstream*, or against *HEAD* when it has none - neither of which is the default branch. It gets
   the answer wrong in both directions: it refuses a branch that is an ancestor of the default branch
   while HEAD happens to lag behind it, and it cannot recognise a rebase merge at all. The caller
   establishes the fact that matters against the ref that matters.

**`RemoveWorktreeAsync`** - the submodule escape hatch. `git worktree remove` **rejects any worktree
containing submodules outright**; the check runs before `--force` is even consulted, so a repository
with a submodule could otherwise never have a worktree removed here. The fallback catches
specifically `ex.StdErr.Contains("submodules")`, then **establishes for itself what git would have
enforced**: `git status --porcelain --ignore-submodules=none` must be empty, or it throws
`RefusedException` and deletes nothing. Only then `Directory.Delete(recursive)` plus `git worktree
prune` - because **the administrative entry outlives the directory, and the branch stays checked out
as far as git is concerned until it is gone.** Note this is *stricter* than git's own `--force`,
which would discard uncommitted work.

**`PullBranchAsync`** never merges. Divergence needs a rebase, which is a different decision offered
separately. A checkout that holds the branch must move *with* it rather than be left behind.

**`PushBranchAsync`**: `remote == local` -> up to date; fast-forward -> plain push; otherwise `git
push --force-with-lease`. **Nothing here fetches first, deliberately** (`:857`): fetching would
refresh the very ref the lease is compared against and turn `--force-with-lease` back into a plain
`--force`. The lease is what makes this safe.

#### The rebase driver

```csharp
public async Task<RebaseResult> RebaseBranchAsync(string branch, string onto,
    IProgress<string>? progress = null, CancellationToken ct = default);
```

1. **Refuse if an operation on that branch is already in progress** - and the check is **by branch,
   not by checkout**. A checkout in the middle of a rebase is *detached*, so it does not look like it
   holds the branch at all; that is how a retry used to reach git and come back with a fatal about a
   `rebase-merge` directory, having never run the merge tool.
2. `before = rev-parse <branch>` - the recovery point.
3. Find the checkout holding the branch; if none, `git worktree add --quiet <tmpdir> <branch>`.
   **This worktree is not detached** - it must hold the branch, because that is what the rebase
   moves. (Contrast `WorktreeManager`, which always detaches.) The `stampeded-rebase-` prefix is how
   `ListInProgressAsync` later recognises a scratch checkout.
4. `git rebase <onto>`.
5. On failure with **nothing unmerged**, the rebase never started - nothing is in progress to abort,
   the branch is untouched, so rethrow.
6. Otherwise drive `ResolveConflictsAsync`. If it does not finish, **the scratch worktree is
   deliberately left behind**, because the rebase is still in progress in it and discarding it would
   throw away the resolutions the user just made.
7. `finally`: a scratch worktree not being left in place is removed with `worktree remove --force`,
   falling back to `worktree prune` **plus `Directory.Delete`** - pruning deregisters but does not
   remove the directory.

**`ResolveConflictsAsync`** - the conflict loop, up to **50 steps** (a rebase stops once per
conflicting commit). Each step: snapshot the conflicted paths, run `git mergetool -y` (**`-y`
because git otherwise prompts on a terminal this process does not have**), then three checks in
order: still unmerged -> give up; **conflict markers still in the files** -> give up; `git rebase
--continue` with `GIT_EDITOR=true`.

Check two is the one that matters (`:789`): *git marks a file resolved when the tool exits without
saying otherwise - for most tools that means "the file was touched", which an editor closed without
a decision also does. Continuing on that word commits the markers themselves, and the rebase reports
success. What the file says is the only thing that cannot be faked.* `WithConflictMarkersAsync`
requires **both** `<<<<<<<` and `>>>>>>>` at the **start of a line**: one alone is ordinary text
often enough (a diff quoted in a comment).

`progress` exists because the merge tool is a child process with no terminal of its own and a window
that need not come to the front, so a rebase stopped on a conflict otherwise looks like one that
stopped responding.

#### In-progress detection, by reading `.git` directly

**No git process is started to answer this.** `AdminDirectoryAsync` resolves `<wt>/.git` - a
directory for the main worktree, a file containing `gitdir: <path>` for a linked one - and
`FindOperation` tests for:

| Marker | Operation |
| --- | --- |
| `rebase-merge/` or `rebase-apply/` | Rebase |
| `MERGE_HEAD` | Merge |
| `CHERRY_PICK_HEAD` | CherryPick |
| `REVERT_HEAD` | Revert |
| `BISECT_LOG` | Bisect |

Why: *a repository with forty worktrees is a repository where one process per worktree is the
difference between a check that can run whenever the window is focused and one that cannot.* This is
polled on focus.

`RebasingBranchAsync` reads `<admin>/rebase-merge/head-name` and strips `refs/heads/`, because **a
rebase detaches HEAD while it runs, so the worktree listing reports no branch for exactly the
checkout that has one at stake. Git wrote it down; read it.**

`ListInProgressAsync` asks **every** checkout git knows about, not just the ones this tool made: a
reader who merged by hand in their own checkout is stuck in exactly the same way.

## Diff

### DiffModel

```csharp
public enum FileChangeKind { Modified, Added, Deleted, Renamed }
public sealed record GeneratedSource(string? BaseFile, string? HeadFile);
public sealed record FileDiff(string OldPath, string NewPath, FileChangeKind Kind, bool IsBinary,
                              IReadOnlyList<DiffHunk> Hunks, GeneratedSource? Generated = null)
public sealed record DiffHunk(int OldStart, int OldLength, int NewStart, int NewLength,
                              string Header, IReadOnlyList<PatchLine> Lines);
public enum PatchLineKind { Context, Added, Removed }
```

- **`Path` is the display key and the review-state key**: `NewPath`, except `OldPath` for a
  deletion. A rename has two paths; a deletion is only ever asked about by its old one.
- `GeneratedSource` holds **filesystem paths, not git paths** - a generated file is not in git.
- `IsGenerated` earns its own concept because such a file *has no history to blame, no place on the
  host to carry a comment, and no claim on the reader's time in the way handwritten code has.*
- `DiffHunk.Header` is git's trailing function-context hint, not the `@@` text.

### ChangedLines

```csharp
public IReadOnlySet<int> Added(string path);              // new-side lines added
public IReadOnlySet<int> Removed(string oldPath);         // old-side lines removed
public IReadOnlySet<int> CommentableNew(string path);     // every new-side line a hunk printed
public IReadOnlySet<int> CommentableOld(string oldPath);  // every old-side line a hunk printed
```

**The "commentable" concept:** a comment can be attached to **every line a hunk prints on that side,
context included** - because that is what the host will take a comment on. Not just the changed
lines. Getting this wrong means offering a comment the host rejects.

**The single-walk rule.** A hunk carries its own starting line numbers and **nothing else does**, so
following them is the only way to turn a run of `+`/`-` markers into numbers. All four sets come
from **one walk** (`:36`) precisely because *doing that walk in more than one place is how two
answers about the same diff come to disagree.* If you need a fifth question about changed lines, add
it to this walk.

### DiffSlider

```csharp
public static List<DiffRun> Shift(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines,
                                  IReadOnlyList<DiffRun> runs);
```

**The problem:** when a run's first line repeats immediately after it, the run can start a line later
and still describe the same change - **the diff is genuinely ambiguous, and the aligner's choice is
arbitrary.** In brace-delimited code this is constant: an added method comes out starting at the
*closing brace of the method before it* and ending inside itself. Valid, and unreadable.

The algorithm, per run:

- Only a **one-sided** run can slide (insert or delete); a replacement is anchored by the lines it
  stands against. **Both neighbours must be matches**, since sliding trades lines with them.
- `MaxUp`: each step moves the run's last line out and the line before it in, which **only describes
  the same change while those two are equal** - `lines[start-s-1] == lines[start+length-s-1]`.
  Bounded by the previous match run's length. `MaxDown` is the mirror.
- `BestShift` evaluates `Rank` at every reachable position, keeping any rank **better or equal**;
  ascending order plus `<= 0` means **ties resolve to the largest (most downward) shift**.
- `Rank(lines, start) = (StartsParagraph ? 0 : 1, Indent)` - *a paragraph boundary first, then the
  shallower indentation.* Lower is better. A block that starts where the code steps outward reads as
  a block.

**Why ties go downward:** *an inserted block belongs after the closing line of the one before it
rather than at it, which is the whole shape of the problem in brace-delimited code: two members that
end alike leave the cut free to sit on either one's brace, and only the later reads as the new
member.*

Called from exactly one place: `DiffDocumentBuilder.Align`, between `DiffLib.Diff.CalculateSections`
and `DiffLib.Diff.AlignElements`.

### DiffDocumentModel

```csharp
public enum DiffLineKind { Context, Added, Removed, Filler, Comment }
public readonly record struct IntraLineSpan(int Start, int Length);
public readonly record struct DiffLineTag(DiffLineKind Kind, int OldLine, int NewLine,
                                          IReadOnlyList<IntraLineSpan>? WordDiffs);
public readonly record struct HunkSpan(int FirstDocLine, int LastDocLine);   // 1-based inclusive
```

`OldLine`/`NewLine` are **1-based blob line numbers, 0 when the line does not exist on that side**.
Everything in the layer keys off that: `Context` both non-zero; `Added` `OldLine == 0`; `Removed`
`NewLine == 0`; `Filler` (side-by-side padding) both 0; `Comment` (synthetic thread row) both 0.

**The invariant that makes the whole editor work** (class doc, `:132`):

> The full NEW file text with REMOVED lines interleaved as verbatim old-blob lines. **Every document
> line is a verbatim copy of a blob line**, so `(docLine, column)` maps exactly to `(blobLine,
> column)` on whichever side the line exists. All position translation between the editor and the
> old/new blobs goes through this map.

No `+`/`-` prefix column, no padding, no tab expansion. That is why a semantic provider's answer at
`(line, character)` can be carried straight onto a document row, and why a comment anchor round-trips
exactly.

`GetSideText(oldSide)` reconstructs one side's text by dropping rows where that side's line number is
0, and returns the parallel `SideToDocLine` map. **The document itself is not valid source of either
side** (removed lines interleave), so anything that wants to parse - the structure provider, the fold
computation, the syntax painter - takes the reconstructed side text and maps results back.

**`WithThreadLines` is a pure splice.** It inserts a synthetic row `@@thread:<key>@@` **below** each
anchor's document row; the view replaces that text with an interactive control. The diff is **not**
recomputed. An anchor with `BlobLine == 0` (an outdated comment) is pinned before the first line. The
inserted row gets `DiffLineKind.Comment` with **both line numbers 0**, so nothing that reads a side's
own text sees it. Hunk spans shift through a prefix-sum array; the asymmetry at `:256` is deliberate:
**an insertion sits below its anchor, so a hunk ending exactly at the anchor stretches over the
thread while a hunk starting after it just moves down.**

**`SideBySideModel`** keeps **equal line counts on both sides, always**, with `Filler` rows. A thread
row must be inserted into **both** documents at the same index (`:72`): *the two panes are kept in
step by copying one scroll offset to the other, which is only exact while they hold the same number
of rows.*

**`DiffDocumentBuilder`:**

- `#nullable disable warnings` covers the whole builder (`:276`): DiffLib annotates its generic
  parameters as `IList<T?>`, making every call site a nullability mismatch for `T = string`.
- `Align` is the shared pipeline: `CalculateSections` -> `DiffRun[]` -> `DiffSlider.Shift` ->
  `DiffSection[]` -> `AlignElements` with `StringSimilarityDiffElementAligner`. The aligner is what
  turns a `Delete`+`Insert` pair of *similar* lines into a `Replace`/`Modify` element, which is what
  makes word-level highlighting possible.
- `Build` emits GitHub-style: within a changed run, **all removals first, then all additions**.
- **`SplitLines`** encodes git's line model:
  ```csharp
  if (text.Length == 0) return [];            // an empty blob has NO lines
  text = text.ReplaceLineEndings("\n");
  if (text.EndsWith('\n')) text = text[..^1]; // exactly ONE trailing newline stripped
  ```
  `"a\n"` is **one** line. Get this wrong and every added or deleted file gains a phantom trailing
  line.
- **`Tokenize`** splits a line into a run of identifier characters, a run of whitespace, or one
  character of anything else. Why not per-character: *comparing single characters makes a renamed
  identifier light up as fragments of the letters it happens to share with the old name - "oldName"
  against "newName" matching on "N", "ame" and lighting the rest - which reads as noise rather than
  as one thing having been replaced.* `SpanOver` emits **one span per changed run**, not one per
  token.

### PatchDocumentBuilder

Turns a unified patch as git prints it (`git show`, `git diff a b`) into the same
`DiffDocumentModel` a review file uses, so a whole commit reads with the colouring, the line-kind
margin and the hunk navigation *instead of as grey text with `+`/`-` in it*.

**The deliberate difference:** the patch text is kept **verbatim, prefix characters and all**. Two
reasons (`:8`): the document spans many files, so its lines cannot be blob lines of any one of them;
and **the `+`/`-` column is the only thing that still says which file a line belongs to once you
scroll.** This knowingly breaks the `DiffDocumentModel` invariant - anything treating a patch
document as navigable source will be off by one column.

The state machine turns on `inHunk`: **outside a hunk, every line is prose or a header**, tagged
`Context(0,0)`. That is the whole point - *a commit message body is indented, so `' '` and `'-'`
there must not be read as context and removal.* A commit message line starting with `-` is a bullet.

### ContextGaps

```csharp
public const int Context = 5;    // lines left visible on each side of a hunk
public const int Step = 20;      // lines revealed per click, as GitHub does it
public const int MinHidden = 6;  // below this, a control costs more than the lines
```

**This is deliberately *not* folding** (`:15`): *Folds are the code's own structure - types, members,
`#region`s - and a reader collapses and expands them for reasons that have nothing to do with the
diff. Hiding context with the same mechanism made the two fight: expanding a method to read it also
unhid unrelated context, "collapse all" swallowed the change, and the two kinds of region cannot
always nest.* The same warning is repeated in `DiffFolding`'s doc. **Do not merge these two
mechanisms.**

`MinHidden = 6` is a considered number: *a bar standing for three lines costs the reader more
attention than reading the three lines does.*

The algorithm is two passes. **Pass 1** scans for maximal runs of `Context` and trims `Context` lines
off each end - **except at the document edges**, where the run has a hunk on one side only.
`hasChanges == false` returns no gaps at all, which is **how a plain source view stays whole**.

**Pass 2** cuts around declaration headers, only when `declarations` is non-empty. `Headers` picks
the declarations that **contain a change** - *a type declared five hundred lines above still says
what the change is part of, while the member just above the one being changed says nothing about it
however close it sits.* The containment test is a **prefix sum of changed lines** (`:87`), so asking
about a range is one subtraction. `Split` cuts one gap around the headers inside it: what lies above
a header stays hidden, the header is shown, and what lies between it and the next header is hidden
only when it clears `MinHidden`.

`RevealTop`/`RevealBottom` are pure: `null` means the gap opened completely and the control
disappears.

### DiffFolding

```csharp
public sealed record FoldRange(int StartLine, int EndLine, string Name, bool DefaultClosed,
                               bool FromHeaderEnd, int HeaderEndLine);
```

Expressed in **document lines, before being turned into offsets** against a particular editor's
document - *the side-by-side view installs the same ranges in two editors whose line lengths differ,
so ranges and offsets have to stay separate.*

- `FromHeaderEnd` - fold from the *end* of the first line, so a member's signature stays visible
  while its body collapses.
- `HeaderEndLine` - the last line of the declaration itself. *A header wrapped over several lines is
  one thing to read.*

`Members` is a pure coordinate translation from `MemberFoldRegion` (1-based side lines, from
`ISemanticProvider`) through `sideToDocLine` into document lines. The indirection exists because *a
diff document is not valid source of either side, so the regions are found in one side's own text -
by whichever provider serves that language - and mapped back here through the line map.*

`FoldRange` is also the input type for `ContextGaps.Compute(declarations:)` - the same records feed
both mechanisms even though the mechanisms are kept apart.

## Call graph inside the layer

```
GitService
 |- ExternalTool.RunAsync                     (every git invocation)
 |- GitDiffParser.Parse                       <- DiffAsync, DiffWorkingTreeAsync
 |- GitLogParser.Parse                        <- LogAsync, LogPickaxeAsync
 |- GitLogParser.ParseShortStat               <- GetCommitStatsAsync
 |- GitLogParser.CountPathTouches             <- GetChurnAsync
 |- GitLogParser.ParseBranches                <- ListBranchesAsync, ListStashesAsync
 |- GitLogParser.ParseNameStatus              <- DiffNameStatusAsync
 |- GitBlameParser.Parse                      <- BlameAsync
 |- BranchSync.From                           <- GetSyncStateAsync
 \- GitHub.GitHubUrl.TryParse                 <- GetOriginOwnerAsync

WorktreeManager  -> CachePath.For("worktrees"), ExternalTool.RunAsync, CliLog.Write
GitBlobReader    -> CliLog.Write; raw Process, NOT ExternalTool

DiffDocumentBuilder
 |- Align -> DiffLib.CalculateSections, DiffSlider.Shift, DiffLib.AlignElements
 |- Build / BuildPair -> Align, SplitLines, ComputeWordDiffs -> Tokenize -> SpanOver
 \- Build -> ComputeHunks
PatchDocumentBuilder.Build -> ParseHunkStarts, DiffDocumentBuilder.ComputeHunks
ContextGaps.Compute -> Add, Headers, Split    (Headers/Split take FoldRange from DiffFolding)
```

Test coverage in `tests/Stampeded.Core.Tests/`: `ChangedLinesTests`, `ContextGapTests`,
`DiffDocumentBuilderTests`, `SideBySideBuilderTests`, `DiffFoldingTests`, `DiffSliderTests`,
`PatchDocumentBuilderTests`, `ThreadLineTests`, `GitBlobReaderTests`, `WorktreeCacheTests`,
`GuessFileTypeTests`, `LogFileRefsTests`, `SolutionTargetTests`, plus `GitRebaseTests` /
`GitPushTests` which build real repositories in temp directories.
