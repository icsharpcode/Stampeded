# Stampeded! developer documentation

A maintenance manual for the codebase: what each layer does, what its public surface is, and - where
it matters - *why* the code is shaped the way it is. Written to be read cold.

Start with [architecture.md](architecture.md). The rest can be read in any order.

| Document | Covers |
| --- | --- |
| [architecture.md](architecture.md) | the four projects, the layering, the five decisions that shape everything, where state lives on disk, environment switches, build and test |
| [review-session.md](review-session.md) | `ReviewWorkspace`, `ReviewScopes`, `ReviewComments`, `MainViewModel`, startup - how a PR number becomes a window full of documents |
| [git-and-diff.md](git-and-diff.md) | `Stampeded.Core/{Git,Diff,Infra}` - the git plumbing, the two diff representations, folding and context gaps, the process runner and the log |
| [pull-request-hosts.md](pull-request-hosts.md) | `IPullRequestHost` and its two implementations, the data model, `PrCache`, `CommentAnchor`, `ReviewStateStore`, the merge queue |
| [semantics.md](semantics.md) | `ISemanticProvider`, the Roslyn workspace, the LSP client, language-server discovery and bootstrap, the Python interpreter, decompilation, the test runner and its parsers |
| [ui.md](ui.md) | the Avalonia layer: docking, documents, panes, the AvaloniaEdit extension points, syntax painting, comment threads, the keyboard model, the screenshot harness |

`CLAUDE.md` in the repository root is the short orientation version of the same material.

## The shortest possible tour

A review is opened (`ReviewWorkspace.OpenPrAsync`), which fetches the PR head, computes the merge
base, and asks git for the diff. That diff - `IReadOnlyList<FileDiff>` - is the review. Opening a
file reads both blobs out of the object database through one long-lived `git cat-file --batch`, and
re-diffs them in process into a `DiffDocumentModel` whose every line is a verbatim blob line. That
invariant is what lets a semantic provider's `(line, column)` answer be carried straight onto a
document row.

In the background, a Roslyn workspace loads over a detached worktree of the head, and a second one is
*derived* from it with the review's files reading as they did at the base - so removed code is
navigable without a second checkout. A language server is started per other language the review
actually touches.

The reader walks the file list, marking files viewed. That, and any draft comments, go into a JSON
file keyed by the repository and the PR number, stamped with the head they were read at. When the
author pushes, the next open notices the head moved, carries over the viewed flags for files the push
did not touch, and can show the diff *since that pass alone* - by replaying the work already read
onto the current base as a tree, which is the only thing that survives a rebase.

Submitting the review batches the drafts that still sit on commentable lines into one host call, and
keeps the rest local.
