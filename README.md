# Stampeded!

A keyboard-driven desktop code-review tool: PR diffs with real semantic code navigation (go to definition, find references, hover docs), git blame, CI results, and unit-test results in one Avalonia UI.

Built on AvaloniaEdit; editor components adapted from [ILSpy](https://github.com/icsharpcode/ILSpy) (MIT); diff-view concepts inspired by [Aehnlich](https://github.com/Dirkster99/Aehnlich) (MIT). Uses the `git` and `gh` CLIs for repository and GitHub access.

Siegi and Chris recorded a brief [Introduction to Stampeded!](https://youtu.be/r16YIcvLlg4) for you to get a glimpse at what the IRE is capable of.

# Motivation

## What was great in eg gitk, Fork and other tools?

These tools are great if you want to look at and manage your git history and branches in a user interface. However, they focus on changes and usually only show the commits and a diff, there is only little context and you cannot navigate in the diff.

## What was missing in Github pull requests?

Again, Navigation, you can only see the changes. No context, no tooltips, no standard IDE features.

## What does Stampeded! bring to the table compared to a static review tool?

Different views onto diff. See entire change, by-commit, navigate freely (diff embedded in entire codebase). Code-navigation in entire code-base, old (deleted) and new code is navigable (both have semantic information). Stampeded remembers where you left off. 

## How did Stampeded grow after the initial feature set?

* Force-push resistant (before force push with after). Why: because we use that a lot in ILSpy.
* More information in the Start page
* Rebasing of local branch (concept of default branch)

## Why did you choose to use CLI tools instead of API calls?

No need to register an OAuth application anywhere and work with the user's permissions all the time.

Using the git mergetool is very convenient that way (no need to re-implement things that already work nicely).

## Why didn't we bring certain features to Stampeded?

* Infinite scroll => v shortcut
