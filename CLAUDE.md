# CLAUDE.md

Guidance for Claude Code (and future Claude sessions) when working on Stampeded!.

## What this is

A keyboard-driven desktop code-review tool: a PR or local branch is read as a diff with real
semantic navigation (go to definition, find references, hover docs), blame, CI state, test
results and coverage, in one Avalonia window. See `README.md` for the pitch.

## Tech stack

- **Avalonia 12** with the **Simple** theme (not Fluent), **AvaloniaEdit** for the diff views,
  **Dock** for the pane layout, **Markdown.Avalonia** for rendered descriptions.
- **CommunityToolkit.Mvvm** (`[ObservableProperty]`) for view models; `Dock.Model.Mvvm` `Tool` /
  `Document` for panes and documents.
- **Roslyn** (`Microsoft.CodeAnalysis.*`) for source semantics: two workspaces per review, head
  and merge base, so removed code stays navigable.
- **CliWrap** for every external process.
- Target framework `net10.0`. Nullable enabled, implicit usings, `TreatWarningsAsErrors`,
  central package management (a new `PackageReference` needs a `PackageVersion` in
  `Directory.Packages.props`), and `AvaloniaUseCompiledBindingsByDefault` - so a typo in a
  binding path is a build error, not a silent blank.

## Project layout

- `src/Stampeded.Core/` - everything that does not need a UI: git and GitHub access, diff and
  fold building, Roslyn hosting, the review store. No Avalonia reference; keep it that way.
- `src/Stampeded/` - the Avalonia app: panes, documents, controls, view models.
- `tests/Stampeded.Core.Tests/` - NUnit, covering `Stampeded.Core` only. The UI layer has no
  automated tests.
- `Stampeded.slnx` builds all three.

## Everything external is a CLI

`git`, `gh`, `dotnet`, `code` and `xdg-open` are the only ways out of the process, all through
`ExternalTool.RunAsync` (which logs the command, and on failure the first line of its output -
an exit code alone never says what went wrong). There are no API tokens of the tool's own: auth,
SSO and token refresh ride on the user's `gh` login. Keep it that way; do not add an HTTP client
for GitHub.

`CliLog.Write` is the log sink the Log pane shows. Anything a user might have to explain to
someone else belongs in it.

## Invariants worth knowing

- **`GitService` reads never touch the user's working tree or index.** They come from the object
  database, or from a checkout's files for a review of uncommitted work. Writes that need a
  checkout use a throwaway worktree.
- **A branch lives in one checkout at a time.** Anything that moves a branch ref has to ask
  `ListWorktreesAsync` whether some checkout has it: if one does, the operation runs *there*, so
  its working tree and index move with the ref. Updating the ref behind a checkout's back leaves
  it describing a commit the branch no longer points at, which is exactly what git's own refusals
  exist to prevent. `RebaseBranchAsync` and `PullBranchAsync` both do this.
- **Review worktrees are detached** (`WorktreeManager`), under `~/.cache/stampeded/worktrees`, so
  they never hold the branch being reviewed.
- **GitHub is the authority for facts git cannot know**: the viewer's login, a repository's
  default branch. The local `origin/HEAD` is a clone-time snapshot and goes stale.
- **`Stampeded.Core/TreeView/` and `Stampeded/Controls/TreeView/` are vendored from ILSpy.** They
  are meant to stay close to upstream so fixes can move both ways - read
  `src/Stampeded.Core/TreeView/README.md` before changing them, and prefer fixing a bug upstream
  too over diverging.

## Code conventions

- **Tabs**, en-US English, ASCII-only in code and comments.
- **No license headers on new files.** The vendored tree-view files keep their original ILSpy
  headers; nothing else in the repository has one.
- **Comments must stand on their own.** They describe the code as it is, for someone reading the
  file cold. Never reference "the change", "the previous version", "as requested", or anything
  else that only means something inside the conversation that wrote it. A comment explains *why*
  the code is the way it is; the code already says what it does.
- **Report what happened, do not swallow it.** A failed external command surfaces its reason;
  a status line says which of the possible outcomes occurred, not just "done".

## Build and test

Prefix `dotnet` with `OPENSSL_ENABLE_SHA1_SIGNATURES=1` (the local OpenSSL setup needs it):

    OPENSSL_ENABLE_SHA1_SIGNATURES=1 dotnet build Stampeded.slnx
    OPENSSL_ENABLE_SHA1_SIGNATURES=1 dotnet test Stampeded.slnx

Tests that exercise git create real repositories in temp directories and shell out to `git` -
that is deliberate: the interesting behaviour is git's, and a mock would only assert what we
already believe. See `GitRebaseTests` / `GitPushTests` for the fixture shape, including how to
script `merge.tool` so a conflicted rebase runs without anything interactive.

## Verifying UI changes

The app screenshots itself when a trigger file appears, because Wayland blocks external capture
of its window: write the target PNG path into `/tmp/stampeded-screenshot-request`, optionally
followed by command lines (`goto:<path>:<line>`, `pane:<id>`, `commit-scope`, `overview`, `sbs`,
`press:<x>,<y>[:<modifiers>]` / `release:...`, `context:<x>,<y>`, `tooltip:<x>,<y>`,
`folder:<path>|cancel`,
... - see `ScreenshotWatcher`). The file is consumed on capture. Only one instance can serve a
request, so shut down extra instances first.

The capture is of whatever window is in front, so a modal dialog photographs itself; `click:`
searches the newest window first for the same reason. A command that opens something runs
*before* the capture but does not finish before it - anything asynchronous needs a second,
plain screenshot request to be seen.

Two lessons that cost a session each:

- **Position bugs need a driven click, not a screenshot of colours.** Highlighting looked fixed
  while the clickable spans were still wrong. `press:` and `release:` take separate modifiers,
  which is what tells a gesture read from the press apart from one read from the release.
- **Reproduce red before claiming a fix.** A crash that did not reproduce under the first
  hypothesis needed a live selection to trigger; without that step the fix would have been a
  guess.

## Commits

Subject is a phrase describing the change, under ~72 characters, no area prefix. The body
explains *why* - the constraint, the decision, what was rejected - and not what the diff already
shows. An AI-assisted commit ends with `Assisted-by: AGENT:MODEL:HARNESS`, e.g.
`Assisted-by: Claude:claude-opus-5:Claude Code`. Never add `Co-Authored-By:` for the AI, and an
agent must not add `Signed-off-by:`.

There is no pre-commit hook; formatting is whatever the surrounding file does.
