using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Documents;

public sealed record CommitLine(string ShortSha, string Added, string Removed, string Subject)
{
	/// <summary>The pending working-tree row rather than a commit.</summary>
	public bool IsUncommitted { get; init; }

	/// <summary>The whole commit message, for the row's tooltip; the row itself has one
	/// line and this project keeps its reasoning in the body.</summary>
	public string? Message { get; init; }

	public FontWeight Weight => IsUncommitted ? FontWeight.SemiBold : FontWeight.Normal;
}

public sealed record FileCostRow(string Marker, string Added, string Removed, string Minutes, string Churn, string Path);

/// <summary>An individual implementation member, or a test type with member changes aggregated.</summary>
public sealed record MemberRow(IBrush Foreground, string Text, ReviewWorkspace.ChangeMapEntry? Entry);

public sealed record CheckLine(string Marker, string Name, string? Link);

/// <summary>One reviewer's standing, coloured by it.</summary>
public sealed record ReviewerLine(IBrush Foreground, string Text);

public sealed record IssueRef(string Display, int Number, string Title);

public sealed partial class OverviewState : ObservableObject
{
	[ObservableProperty]
	string title = "";

	[ObservableProperty]
	string estimate = "";

	[ObservableProperty]
	string description = "";

	[ObservableProperty]
	string commitsHeader = "";

	[ObservableProperty]
	string filesHeader = "";

	[ObservableProperty]
	string membersHeader = "";

	[ObservableProperty]
	string checksHeader = "CI: not loaded yet.";

	[ObservableProperty]
	bool checksFailing;

	[ObservableProperty]
	bool checksGreen;

	[ObservableProperty]
	bool checksPending;

	[ObservableProperty]
	bool checksUnknown = true;

	[ObservableProperty]
	string coverageLine = "Coverage: not measured (Tests pane > Run + Coverage).";

	[ObservableProperty]
	string testsLine = "Tests: not run in this session (Tests pane).";

	[ObservableProperty]
	string reviewersHeader = "";

	[ObservableProperty]
	string workingTreeLine = "";

	/// <summary>Commits are reference material and start folded, unless there is
	/// uncommitted work - which is the one entry worth arriving on.</summary>
	[ObservableProperty]
	bool commitsExpanded;

	/// <summary>True while the change is being read one commit at a time.</summary>
	[ObservableProperty]
	bool inCommitScope;

	[ObservableProperty]
	string commitScopeLine = "";

	[ObservableProperty]
	bool canEnterCommitScope;

	/// <summary>True while the review is narrowed at all - by a commit, or to what has
	/// changed since the last pass.</summary>
	[ObservableProperty]
	bool inScope;

	[ObservableProperty]
	bool canEnterSinceLastPass;

	/// <summary>Set while the review was opened from a snapshot: what is exact, what is old,
	/// and how to get the rest.</summary>
	[ObservableProperty]
	string offlineLine = "";

	[ObservableProperty]
	string toolStatus = "";

	[ObservableProperty]
	bool hasFixtureTools;
}

/// <summary>
/// The review brief, docked as a document and opened once a review loads: description,
/// linked issues, commits, per-file cost/churn, changed members, CI state and coverage -
/// everything the reading phases used to spread over wizard pages, in one scrollable
/// surface with the panes available beside it.
/// </summary>
public class OverviewDocumentViewModel : Document
{
	static readonly IBrush Added = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#3794FF"));
	static readonly IBrush Removed = new SolidColorBrush(Color.Parse("#F85149"));

	readonly ReviewWorkspace workspace;

	public OverviewState State { get; } = new();
	public ObservableCollection<IssueRef> LinkedIssues { get; } = [];
	public ObservableCollection<CommitLine> CommitLines { get; } = [];
	public ObservableCollection<FileCostRow> FileCosts { get; } = [];
	public ObservableCollection<MemberRow> ImplMembers { get; } = [];
	public ObservableCollection<MemberRow> TestGroups { get; } = [];
	public ObservableCollection<CheckLine> CheckLines { get; } = [];
	public ObservableCollection<ReviewerLine> Reviewers { get; } = [];

	public OverviewDocumentViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		Title = "Overview";
		// The review's home tab: pinned first; teardown unpins before closing.
		CanClose = false;
		workspace.ReviewChanged += () => Dispatcher.UIThread.Post(RebuildAll);
		workspace.ChurnChanged += () => Dispatcher.UIThread.Post(RebuildFiles);
		workspace.ChangeMapChanged += () => Dispatcher.UIThread.Post(OnChangeMap);
		workspace.ChecksLoaded += () => Dispatcher.UIThread.Post(RebuildChecks);
		workspace.ReviewersChanged += () => Dispatcher.UIThread.Post(RebuildReviewers);
		workspace.CoverageChanged += () => Dispatcher.UIThread.Post(RebuildCoverage);
		workspace.TestResultsChanged += () => Dispatcher.UIThread.Post(RebuildTests);
		workspace.StatusMessage += message => Dispatcher.UIThread.Post(() => State.ToolStatus = message);
		workspace.Scopes.Changed += () => Dispatcher.UIThread.Post(RebuildCommitScope);
		State.HasFixtureTools = workspace.HasDecompilerTestCases;
		RebuildAll();
	}

	public void OpenInVsCode() => workspace.OpenInVsCodeAsync(oldSide: false).HandleExceptions();

	public void RefreshChecks()
	{
		if (workspace.CurrentPr is null)
			return;
		State.ChecksHeader = "CI: refreshing...";
		workspace.RequestChecksRefresh();
	}

	public void OpenFixturesInIlspy() => workspace.OpenAffectedFixturesInILSpyAsync().HandleExceptions();

	void RebuildAll()
	{
		State.Title = workspace.CurrentPr is { } pr
			? $"#{pr.Number}  {pr.Title}"
			: workspace.LocalRange is { } range
				// The merge base, not the base ref: the diff is against where the branch left
				// its base, which is a different commit as soon as the base moved on.
				? $"{range.Head}  vs  {range.Base}  (merge base {workspace.BaseSha?[..8]})"
				: workspace.HeadSha is null ? "No review open." : "Local range review";
		State.WorkingTreeLine = workspace.DirtyWorktreePath is { } dirty
			? $"Head is the working tree at {dirty} - uncommitted changes included"
			+ (workspace.UncommittedFileCount > 0 ? $" ({workspace.UncommittedFileCount} file(s) beyond the last commit)." : ".")
			: "";
		State.OfflineLine = workspace is { Offline: true, OfflineSince: { } since }
			? $"Offline: opened from the snapshot taken {since:g}. The change and its diff are exact; "
				+ "the description, comments, checks and reviews are as old as the snapshot, and the "
				+ "verdict cannot be given until GitHub is reachable. F5 tries again."
			: "";
		State.Description = workspace.CurrentPr?.Body is { Length: > 0 } body
			? Core.GitHub.IssueLinks.Autolink(body.ReplaceLineEndings("\n"), workspace.IssueUrlPrefix)
			: "(no description)";
		RebuildCommitScope();
		RebuildLinkedIssuesAsync().HandleExceptions();
		RebuildFiles();
		RebuildChecks();
		RebuildReviewers();
		RebuildCoverage();
		RebuildTests();
		OnChangeMap();
		LoadCommitsAsync().HandleExceptions();
	}

	void RebuildCommitScope()
	{
		State.CanEnterCommitScope = workspace.Scopes.CanEnterCommit;
		State.InCommitScope = workspace.Scopes.Commit is not null;
		State.InScope = workspace.Scopes.InScope;
		State.CanEnterSinceLastPass = workspace.Scopes.CanEnterSinceLastPass;
		State.CommitScopeLine = workspace.Scopes.Commit is { } commit
			? $"Commit {workspace.Scopes.CommitIndex + 1} of {workspace.Scopes.Series.Count}:  "
				+ $"{commit.ShortSha}  {commit.Subject}"
			: workspace.Scopes.InSinceLastPass ? workspace.Scopes.ScopeLine
			: "";
	}

	public void EnterCommitScope() => workspace.Scopes.EnterCommitAsync().HandleExceptions();

	public void EnterSinceLastPass() => workspace.Scopes.EnterSinceLastPassAsync().HandleExceptions();

	public void StepCommitScope(int direction) => workspace.Scopes.StepCommitAsync(direction).HandleExceptions();

	public void ExitCommitScope() => workspace.Scopes.ExitAsync().HandleExceptions();

	/// <summary>
	/// The issues a description points at. A "#123" in prose is only a candidate - a colour
	/// like "#141414" and a "#0" that answers to nothing read exactly the same - so each one
	/// is put to GitHub and only the numbers something answers to are shown, carrying the
	/// title they answered with.
	/// </summary>
	async Task RebuildLinkedIssuesAsync()
	{
		// Asking takes a moment, and the page is rebuilt on several events: the answers are
		// collected aside and shown at once, and a pass that has been overtaken drops them.
		int pass = ++linkedIssuesPass;
		if (workspace.CurrentPr?.Body is not { } body || !workspace.Comments.CanComment)
		{
			LinkedIssues.Clear();
			return;
		}
		var found = new List<IssueRef>();
		foreach (int number in Regex.Matches(body, @"#(\d{1,6})\b")
			.Select(m => int.Parse(m.Groups[1].Value))
			.Where(n => n != workspace.CurrentPr?.Number)
			.Distinct()
			.Take(12))
		{
			if (await workspace.GitHub.GetIssueTitleAsync(number) is { } title)
				found.Add(new IssueRef($"#{number}", number, title));
		}
		if (pass != linkedIssuesPass)
			return;
		LinkedIssues.Clear();
		foreach (var issue in found)
			LinkedIssues.Add(issue);
	}

	int linkedIssuesPass;

	public void OpenIssue(IssueRef issue)
		=> ExternalTool.RunAsync("gh", ["browse", issue.Number.ToString()], workspace.RepoPath).HandleExceptions();

	void RebuildFiles()
	{
		var totals = workspace.ComputeTriage();
		State.Estimate = totals.Rows.Count == 0 ? "" :
			$"Weighted estimate: ~{totals.Minutes} min = {totals.Sittings} sitting(s)  " +
			$"(implementation {totals.ImplChanged} line(s) @5/min, tests {totals.TestChanged} @15/min, " +
			$"generated {totals.GeneratedChanged} @50/min, {totals.DependencyFiles} manifest file(s) flat).";
		FileCosts.Clear();
		foreach (var row in totals.Rows.Take(20))
		{
			string marker = row.Category switch {
				Core.Review.FileCategory.Test => "test",
				Core.Review.FileCategory.Dependency => "deps",
				Core.Review.FileCategory.Generated => "gen",
				_ => "impl",
			};
			int churn = workspace.ChurnByFile?.GetValueOrDefault(row.Path) ?? 0;
			FileCosts.Add(new FileCostRow(marker, $"+{row.Added}", $"-{row.Removed}", $"~{row.Minutes} min",
				churn > 0 ? $"{churn}x/yr" : "", row.Path));
		}
		if (totals.Rows.Count > 20)
			FileCosts.Add(new FileCostRow("", "", "", "", "", $"... and {totals.Rows.Count - 20} more file(s)"));
		State.FilesHeader = totals.Rows.Count == 0 ? "" : $"{totals.Rows.Count} file(s); high churn deserves extra caution";
	}

	async Task LoadCommitsAsync()
	{
		CommitLines.Clear();
		State.CommitsHeader = "";
		// The review's range, not the displayed one: while a commit is in scope BaseSha and
		// HeadSha describe that commit, and this section is about the series it belongs to.
		if (workspace.Scopes.ReviewRange is null)
			return;
		try
		{
			// Uncommitted work leads the list: it sits on top of every commit below it, and
			// it is the part nobody else can see yet.
			var uncommitted = await LoadUncommittedLineAsync();
			State.CommitsExpanded = uncommitted is not null;
			if (uncommitted is not null)
				CommitLines.Add(uncommitted);
			var commits = await workspace.Scopes.GetRangeCommitsAsync();
			var stats = await workspace.Scopes.GetRangeCommitStatsAsync();
			foreach (var commit in commits.Take(8))
			{
				var (added, removed) = stats.GetValueOrDefault(commit.Sha);
				// The one being read is marked, so the series says where in it you are.
				bool inScope = workspace.Scopes.Commit?.Sha == commit.Sha;
				CommitLines.Add(new CommitLine(commit.ShortSha, $"+{added}", $"-{removed}",
					inScope ? $"> {commit.Subject}" : commit.Subject) {
					Message = commit.Message,
				});
			}
			if (commits.Count > 8)
				CommitLines.Add(new CommitLine("", "", "", $"... and {commits.Count - 8} more (Commits pane)"));
			State.CommitsHeader = (commits.Count, uncommitted) switch {
				(0, null) => "",
				(0, _) => "uncommitted only",
				(_, null) => $"{commits.Count} commit(s)",
				_ => $"{commits.Count} commit(s) + uncommitted",
			};
		}
		catch (ToolFailedException)
		{
			// No commit summary is fine; the Commits pane still works.
		}
	}

	void OnChangeMap()
	{
		ImplMembers.Clear();
		TestGroups.Clear();
		var map = workspace.ChangeMap;
		foreach (var entry in map.Where(e => !Core.Review.TestPaths.IsTestPath(e.RelPath))
			.OrderBy(e => e.RelPath).ThenBy(e => e.Line))
		{
			var brush = entry.Kind switch {
				"Added" => Added,
				"Removed" => Removed,
				_ => Modified,
			};
			ImplMembers.Add(new MemberRow(brush, $"{entry.Kind[0]}  {entry.Display}", entry));
		}
		foreach (var group in map.Where(e => Core.Review.TestPaths.IsTestPath(e.RelPath))
			.GroupBy(e => e.Display.Split('.')[0])
			.OrderByDescending(g => g.Count()))
		{
			int added = group.Count(e => e.Kind == "Added");
			int modified = group.Count(e => e.Kind == "Modified");
			int removed = group.Count(e => e.Kind == "Removed");
			var parts = new List<string>();
			if (added > 0)
				parts.Add($"{added} added");
			if (modified > 0)
				parts.Add($"{modified} modified");
			if (removed > 0)
				parts.Add($"{removed} removed");
			TestGroups.Add(new MemberRow(Brushes.Gray,
				$"{group.Key}  -  {string.Join(", ", parts)} member(s)",
				group.OrderBy(e => e.Line).First()));
		}
		State.MembersHeader = !workspace.ChangeMapComputed
			? "waiting for semantics..."
			: map.Count == 0
				? "none (non-code changes only)"
				: $"{map.Count} member(s); {ImplMembers.Count} implementation, tests grouped by type";
	}

	void RebuildChecks()
	{
		CheckLines.Clear();
		var checks = workspace.Checks;
		if (checks is null)
		{
			State.ChecksFailing = false;
			State.ChecksGreen = false;
			State.ChecksPending = false;
			State.ChecksUnknown = true;
			State.ChecksHeader = workspace.CurrentPr is null ? "CI: local review - none." : "CI: loading...";
			return;
		}
		State.ChecksUnknown = false;
		// Cancelled runs are treated as failures: they did not vouch for the change.
		int failing = checks.Count(c => c.Bucket is "fail" or "cancel");
		int pending = checks.Count(c => c.Bucket == "pending");
		State.ChecksFailing = failing > 0;
		State.ChecksPending = failing == 0 && pending > 0;
		State.ChecksGreen = failing == 0 && pending == 0;
		State.ChecksHeader = failing > 0
			? $"CI: {failing} of {checks.Count} check(s) FAILING{(pending > 0 ? $", {pending} in progress" : "")} - is this ready for review?"
			: pending > 0
				? $"CI: {pending} of {checks.Count} check(s) still in progress."
				: $"CI: all {checks.Count} check(s) passing or skipped.";
		foreach (var check in checks.Where(c => c.Bucket == "fail").Take(10))
			CheckLines.Add(new CheckLine("FAIL", check.Name, check.Link));
	}

	static readonly IBrush ApprovedBrush = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush ChangesBrush = new SolidColorBrush(Color.Parse("#F85149"));
	static readonly IBrush StaleBrush = new SolidColorBrush(Color.Parse("#D29922"));

	/// <summary>
	/// Who has approved and who has asked for changes. A verdict given on an earlier head is
	/// marked as one: it says what somebody thought of code that has since been rewritten,
	/// which is not the same as their opinion of what is on screen.
	/// </summary>
	void RebuildReviewers()
	{
		Reviewers.Clear();
		if (workspace.CurrentPr is null)
		{
			State.ReviewersHeader = "";
			return;
		}
		if (workspace.Reviewers is not { } verdicts)
		{
			State.ReviewersHeader = "could not be read";
			return;
		}
		string head = workspace.Scopes.ReviewRange?.Head ?? workspace.HeadSha ?? "";
		foreach (var verdict in verdicts)
		{
			bool stale = verdict.CommitId is { Length: > 0 } commit && commit != head;
			string said = verdict.Approved ? "approved" : "requested changes";
			Reviewers.Add(new ReviewerLine(
				stale ? StaleBrush : verdict.Approved ? ApprovedBrush : ChangesBrush,
				stale ? $"{verdict.Author} {said} at {verdict.CommitId![..9]}" : $"{verdict.Author} {said}"));
		}
		int approved = verdicts.Count(v => v.Approved);
		int changes = verdicts.Count - approved;
		State.ReviewersHeader = verdicts.Count == 0
			? "nobody has reviewed this yet"
			: $"{approved} approval(s), {changes} asking for changes";
	}

	void RebuildTests()
	{
		State.TestsLine = workspace.LastTestSummary is { } summary
			? $"Tests: {summary}."
			: "Tests: not run in this session (Tests pane).";
	}

	void RebuildCoverage()
	{
		if (workspace.Coverage is null)
		{
			State.CoverageLine = "Coverage: not measured (Tests pane > Run + Coverage).";
			return;
		}
		var (uncovered, measured) = workspace.UncoveredAddedLines();
		State.CoverageLine = $"Coverage: {uncovered} uncovered of {measured} measured added line(s).";
	}

	public void OpenMember(MemberRow row)
	{
		if (row.Entry is { } entry)
			workspace.NavigateToFileLineAsync(entry.RelPath, entry.Line, entry.OldSide, record: true).HandleExceptions();
	}

	public void OpenFileRow(FileCostRow row)
	{
		var file = workspace.Files.FirstOrDefault(f => f.Path == row.Path);
		if (file is not null)
			workspace.OpenFileAsync(file).HandleExceptions();
	}

	public void OpenPrOnGitHub()
	{
		if (workspace.CurrentPr is { } pr)
			workspace.OpenOnGitHubAsync(pr.Number).HandleExceptions();
	}

	/// <summary>The working tree as a pending entry above the commits, when the review's
	/// head is a checkout rather than a commit. Null when there is nothing uncommitted.</summary>
	async Task<CommitLine?> LoadUncommittedLineAsync()
	{
		if (workspace.DirtyWorktreePath is not { } dirty)
			return null;
		var files = await workspace.Git.DiffWorkingTreeAsync(dirty, "HEAD");
		if (files.Count == 0)
			return null;
		int added = 0, removed = 0;
		foreach (var line in files.SelectMany(f => f.Hunks).SelectMany(h => h.Lines))
		{
			if (line.Kind == Core.Diff.PatchLineKind.Added)
				added++;
			else if (line.Kind == Core.Diff.PatchLineKind.Removed)
				removed++;
		}
		return new CommitLine("uncommitted", $"+{added}", $"-{removed}",
			$"{files.Count} file(s) not committed, in {dirty}") { IsUncommitted = true };
	}

	public void OpenCommit(CommitLine line)
	{
		// The working-tree entry names no commit, so there is nothing to open on GitHub.
		if (line.ShortSha.Length > 0 && !line.IsUncommitted)
			workspace.OpenCommitOnGitHubAsync(line.ShortSha).HandleExceptions();
	}

	public void OpenCheck(CheckLine line)
	{
		if (line.Link is { Length: > 0 } url)
			workspace.OpenUrlAsync(url).HandleExceptions();
	}

	public void Bounce() => workspace.PrepareBounceBody();

}
