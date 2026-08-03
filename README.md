# Stampeded!

A keyboard-driven desktop code-review tool: PR diffs with real semantic code
navigation (go to definition, find references, hover docs), git blame, CI results,
and unit-test results in one Avalonia UI.

Built on AvaloniaEdit; editor components adapted from
[ILSpy](https://github.com/icsharpcode/ILSpy) (MIT); diff-view concepts inspired by
[Aehnlich](https://github.com/Dirkster99/Aehnlich) (MIT). Uses the `git` and `gh`
CLIs for repository and GitHub access, and Roslyn for source semantics.

Status: early scaffold (M1).
