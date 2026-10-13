# Stampeded! in eight short tours

Each tour takes under five minutes and stands on its own, so pick whichever one you're
curious about. They assume you review code regularly and want to see what this tool gives
you that a web diff doesn't.

Every screenshot was taken in
[christophwille/stampeded-demo](https://github.com/christophwille/stampeded-demo), a small C#
solution whose pull requests are staged for exactly this and kept open. You can follow every
step there yourself.

| | Tour | What it shows |
| --- | --- | --- |
| 1 | [Open a pull request and read it](01-open-and-read.md) | the start page, the overview, walking files and hunks from the keyboard, viewed flags, unified and side by side |
| 2 | [Navigate the code in the diff](02-navigate-the-code.md) | hover, go to definition, find references, removed code that is still navigable, call graph, a decompiled NuGet type |
| 3 | [Read it commit by commit](03-commit-by-commit.md) | the commit scope, per-commit files, blame, the history of a file |
| 4 | [Comment and submit](04-comment-and-submit.md) | drafts, suggestions, replies, the review page, submitting |
| 5 | [Come back after a force push](05-after-a-force-push.md) | what survives a rebase: viewed flags, comments, and a diff of only what the author changed since |
| 6 | [CI, tests and coverage](06-ci-tests-coverage.md) | failing checks, running the tests of the head, coverage in the gutter, base against head |
| 7 | [Review a local branch](07-local-branches.md) | a review without a pull request, uncommitted work included |
| 8 | [Merge](08-merge.md) | what blocks a merge, merging, the merge queue |

## Before the first tour

You need `git`, the GitHub CLI logged in (`gh auth login`) and the .NET 10 SDK. Stampeded! has
no token of its own; it sees what your `gh` sees.

    dotnet run --project src/Stampeded

Then **Review > Open from URL...** and enter `christophwille/stampeded-demo`. It asks where to
clone to, once.

The keys used throughout are in **Help > Keyboard Shortcuts**. Single letters act whenever the
focus is not in a text box.

## Re-shooting the screenshots

The images are not taken by hand. Each one is a small script under [shots/](shots) - the
commands of the app's own screenshot harness (`ScreenshotWatcher`, see [../ui.md](../ui.md)) -
and [shoot.ps1](shoot.ps1) plays them:

    ../../../stampeded-demo/stage.ps1                      # the pull requests as first read
    ./shoot.ps1 -Start -Demo ../../../stampeded-demo -Fresh
    ./shoot.ps1 '01-*'
    ./shoot.ps1 -Stop

The tours are shot in order, and some depend on what an earlier shot left behind (a file
ticked off, a draft written). Three steps happen outside the app: `stage.ps1 -Push2` between
the first and second shot of tour 5, `stage.ps1 -Local` before tour 7, and tour 8 really
merges a pull request, which `stage.ps1` then opens again under a new number.

Shots that click by position (`press:`, `move:`) hold window coordinates. They are right for
the window size `shoot.ps1` sets and need another look when the layout changes.
