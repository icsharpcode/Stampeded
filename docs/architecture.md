# Architecture

Stampeded! is a keyboard-driven desktop code-review tool. A pull request or a local branch is read
as a diff with real semantic navigation (go to definition, find references, hover docs), blame, CI
state, test results and coverage, in one Avalonia window.

## The four projects

| Project | What it is | May reference |
| --- | --- | --- |
| `src/Stampeded.Core/` | everything that does not need a UI: git and host access, diff and fold building, Roslyn hosting, the LSP client, the review store | no Avalonia - **keep it that way** |
| `src/Stampeded/` | the Avalonia app: panes, documents, controls, view models | Core |
| `src/Stampeded.RoslynLsp/` | Roslyn as a language server, for reading C# out of process | Core |
| `tests/Stampeded.Core.Tests/` | NUnit, covering `Stampeded.Core` only. **The UI layer has no automated tests** | Core |

`Stampeded.slnx` builds all four.

## The layering

```
                          MainWindow / MainViewModel
                                    |
                            ReviewWorkspace          <- the session hub (App.Workspace)
                          /    |      |     \
              ReviewScopes  ReviewComments  Documents/  Panes/
                    \        /                  |
                     \      /              Editor/, Diff/, Controls/
                      \    /                    |
  ------------------------------------------------------------------ Stampeded.Core
        |               |               |               |
   Git/ + Diff/   PullRequests/    Semantics/       Review/
   ExternalTool   GitHub/ AzureDevOps/  Roslyn/ Lsp/   MergeQueue/
                                    Decompilation/ Testing/
                      |
              Infra/ (ExternalTool, CliLog, CachePath, ...)
```

Four interfaces carry all the polymorphism in the codebase, and there are exactly four:

- **`IPullRequestHost`** - GitHub over `gh`, Azure DevOps over `az`. See
  [pull-request-hosts.md](pull-request-hosts.md).
- **`ISemanticProvider`** - Roslyn in process, or a language server over stdio. See
  [semantics.md](semantics.md).
- **`IDecompileTargets`** - a capability a provider *may* also have, deliberately not part of
  `ISemanticProvider`, because only a provider with real metadata behind it can answer.
- **`IDiffDocument` / `IReviewDocumentView`** - the unified and the side-by-side diff layouts. See
  [ui.md](ui.md).

## Five decisions that shape everything

### 1. Everything external is a CLI

`git`, `gh`, `az`, `dotnet`, `code` and `xdg-open` are the only ways out of the process, all through
`ExternalTool.RunAsync` - which logs the command, and on failure the first line of its output, since
an exit code alone never says what went wrong. **There are no API tokens of the tool's own**: auth,
SSO and token refresh ride on the user's `gh auth` and `az login`. Do not add an HTTP client for any
host.

A language server is the one exception, because it is not a command with an exit code: it starts
once and answers until the review closes, over JSON-RPC on its stdin and stdout. Everything it does
still reaches the log.

`CliLog.Write` is the sink the Log pane shows. Anything a user might have to explain to someone else
belongs in it.

### 2. GitHub's words are the model's words

`APPROVE` / `REQUEST_CHANGES` / `COMMENT`, `APPROVED` / `CHANGES_REQUESTED`, `LEFT` / `RIGHT`,
`MERGEABLE` / `BLOCKED`: the panes and the pure functions under them already speak them, and Azure
DevOps - which counts votes from 10 to -10 and has no review object at all - maps onto them inside
its own implementation and nowhere else. Nothing above `IPullRequestHost` knows which host answered;
what a pane shows the reader comes from `HostName`.

### 3. A symbol is a file plus a position

`SymbolRef` carries `(RelPath, Line, Column, Display, Name, IsType, ContainingType?)` - never a
compiler object, because that is all a language server can be handed back. The consequence:
**the position stored in a `SymbolRef` has to re-resolve to the same symbol.**

### 4. Reads never touch the user's working tree

`GitService` reads come from the object database, or from a checkout's files for a review of
uncommitted work. Writes that need a checkout use a throwaway worktree. Review worktrees are
**detached**, under `~/.cache/stampeded/worktrees`, so they never hold the branch being reviewed.

**A branch lives in one checkout at a time.** Anything that moves a branch ref has to ask
`ListWorktreesAsync` whether some checkout has it: if one does, the operation runs *there*, so its
working tree and index move with the ref.

### 5. There are two diffs, and they disagree on purpose

`FileDiff` is git's opinion, parsed from `git diff -U3`, and it decides where a comment may be
anchored - because the host computes its anchors from the same diff. `DiffDocumentModel` is built
from the whole blobs and re-diffed in process, and it is what the reader looks at, so they can
scroll out of a hunk into untouched code. See [git-and-diff.md](git-and-diff.md).

## Where state lives on disk

| Path | Written by | Contents |
| --- | --- | --- |
| `~/.cache/stampeded/worktrees/<repo>/<sha9>` | `WorktreeManager` | detached review checkouts, LRU-capped at 6 |
| `~/.cache/stampeded/prs/<repo>_pr<N>.json` | `PrCache` | the snapshot that lets a review open offline |
| `~/.cache/stampeded/python-lsp/` | `LanguageServers` | a venv with basedpyright, installed on demand |
| `$LOCALAPPDATA/stampeded/reviews/*.json` | `ReviewStateStore` | viewed flags, drafts, pass heads |
| `$LOCALAPPDATA/stampeded/*.txt` | `UserData` | zoom, window placement, recent repos, preferences |
| `refs/stampeded/review/<key>/head` | `GitService.PinReviewHeadsAsync` | pins the head a pass was read at, so a force-push cannot prune it |
| `refs/stampeded/pr/<N>` | `GitService.FetchPrHeadAsync` | the fetched PR head |
| `refs/stampeded/merge-queue` (on origin) | `MergeQueueService` | the shared merge queue |

Deleting any of the cache directories costs a reader nothing but time.

## Environment switches

All optional.

| Variable | Effect |
| --- | --- |
| `STAMPEDED_PR_HOST=github\|azdo` | override the host decided from origin's URL |
| `STAMPEDED_SEMANTICS=lsp` | read C# through `Stampeded.RoslynLsp` instead of in process |
| `STAMPEDED_PYTHON_LSP` / `STAMPEDED_CSHARP_LSP` | a server command line to use instead of the search |
| `STAMPEDED_PYTHON_PATH` | the interpreter, for an environment none of the usual places would find |
| `STAMPEDED_LSP_TRACE=1` | log every request with what came back, and ask the server for trace-level logging. **This is the thing to turn on when a review reads a language on one machine and not on another.** |
| `OPENSSL_ENABLE_SHA1_SIGNATURES=1` | required by the local OpenSSL setup; set process-wide by `Program.Main`, and needed on the command line for `dotnet` |

## Tech stack

- **Avalonia 12** with the **Simple** theme (not Fluent), **AvaloniaEdit** for the diff views,
  **Dock** for the pane layout, **Markdown.Avalonia** for rendered descriptions.
- **CommunityToolkit.Mvvm** (`[ObservableProperty]`) for view models; `Dock.Model.Mvvm` `Tool` /
  `Document` for panes and documents.
- **Roslyn** for source semantics: two workspaces per review, head and merge base, so removed code
  stays navigable.
- **TextMateSharp** for syntax colours: VS Code's grammars and themes. The editor's own `.xshd`
  definitions answer for what the bundle does not carry, which is ILAsm.
- **CliWrap** for every external process. **DiffLib** for the in-process alignment.
  **ICSharpCode.Decompiler** for sourceless definitions.
- Target framework `net10.0`. Nullable enabled, implicit usings, `TreatWarningsAsErrors`, central
  package management (a new `PackageReference` needs a `PackageVersion` in
  `Directory.Packages.props`), and `AvaloniaUseCompiledBindingsByDefault` - so a typo in a binding
  path is a build error, not a silent blank.

`Directory.Build.props` also carries two non-obvious lines:

- `<PackageReference Include="Microsoft.Build.Framework" ExcludeAssets="runtime" PrivateAssets="all" />`
  - MSBuildLocator loads the MSBuild assemblies out of the installed SDK, so the copies Roslyn's
  MSBuild workspace drags in transitively must not land next to ours: two
  `Microsoft.Build.Framework` identities in one process is exactly the load failure the locator
  exists to avoid (MSBL001).
- `<Using Include="Stampeded.Core.Semantics" />` - the semantic vocabulary (tokens, symbols, hits) is
  named unqualified everywhere, the way the framework's own types are.

## Build and test

Prefix `dotnet` with `OPENSSL_ENABLE_SHA1_SIGNATURES=1`:

```
OPENSSL_ENABLE_SHA1_SIGNATURES=1 dotnet build Stampeded.slnx
OPENSSL_ENABLE_SHA1_SIGNATURES=1 dotnet test --solution Stampeded.slnx --report-trx --results-directory test-results
```

`dotnet test` runs through Microsoft.Testing.Platform (`global.json` pins the runner), so the
solution is named with `--solution` - the bare positional form is VSTest syntax and errors.
`--report-trx` leaves a TRX per test assembly under `test-results/`, which is how a failure survives
the run.

As of this writing the suite is **342 tests, 341 passing, 1 skipped** (`PythonServerInstallTests`,
which installs a real language server into a temporary cache) in about 18 seconds.

Tests that exercise git create real repositories in temp directories and shell out to `git` - that is
deliberate: the interesting behaviour is git's, and a mock would only assert what we already believe.
See `GitRebaseTests` / `GitPushTests` for the fixture shape, including how to script `merge.tool` so
a conflicted rebase runs without anything interactive.

CI (`.github/workflows/build.yml`) builds and tests Release on windows-latest, ubuntu-latest and
macos-latest, uploading the TRX files as artifacts.

## Verifying UI changes

The app screenshots itself when a trigger file appears, because Wayland blocks external capture of
its window: write the target PNG path into `/tmp/stampeded-screenshot-request`, optionally followed
by command lines. See the command table in [ui.md](ui.md). Only one instance can serve a request, so
shut down extra instances first.

Two lessons that cost a session each:

- **Position bugs need a driven click, not a screenshot of colours.** Highlighting looked fixed while
  the clickable spans were still wrong. `press:` and `release:` take separate modifiers, which is
  what tells a gesture read from the press apart from one read from the release.
- **Reproduce red before claiming a fix.** A crash that did not reproduce under the first hypothesis
  needed a live selection to trigger; without that step the fix would have been a guess.

## Code conventions

- **Tabs**, en-US English, ASCII-only in code and comments.
- **No license headers on new files.** The vendored tree-view files keep their original ILSpy
  headers; nothing else in the repository has one.
- **Comments must stand on their own.** They describe the code as it is, for someone reading the file
  cold. Never reference "the change", "the previous version", "as requested", or anything else that
  only means something inside the conversation that wrote it. A comment explains *why* the code is
  the way it is; the code already says what it does.
- **Report what happened, do not swallow it.** A failed external command surfaces its reason; a
  status line says which of the possible outcomes occurred, not just "done".
- Commit subject is a phrase describing the change, under ~72 characters, no area prefix. The body
  explains *why* - the constraint, the decision, what was rejected - not what the diff already shows.

## Vendored code

`src/Stampeded.Core/TreeView/` and `src/Stampeded/Controls/TreeView/` are vendored from ILSpy. They
are meant to stay close to upstream so fixes can move both ways - read
`src/Stampeded.Core/TreeView/README.md` before changing them, and prefer fixing a bug upstream too
over diverging. `Navigation/NavigationHistory<T>` and the XML half of `Infra/GuessFileType` come
from the same place. The diff-view concepts are inspired by
[Aehnlich](https://github.com/Dirkster99/Aehnlich) (MIT).
