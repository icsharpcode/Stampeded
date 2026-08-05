using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Documents;

public sealed record CommitLine(string ShortSha, string Added, string Removed, string Subject);

public sealed record FileCostRow(string Marker, string Added, string Removed, string Minutes, string Churn, string Path);

/// <summary>An individual implementation member, or a test type with member changes aggregated.</summary>
public sealed record MemberRow(IBrush Foreground, string Text, ReviewWorkspace.ChangeMapEntry? Entry);

public sealed record CheckLine(string Marker, string Name, string? Link);

public sealed record IssueRef(string Display, int Number);

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
	string sweepHeader = "Consequence sweep: waiting for the change map.";

	[ObservableProperty]
	string toolStatus = "";

	[ObservableProperty]
	bool hasFixtureTools;
}

/// <summary>
/// The review brief, docked as a document and opened once a review loads: description,
/// linked issues, commits, per-file cost/churn, changed members, CI state, coverage and
/// the computed consequence sweep - everything the reading phases used to spread over
/// wizard pages, in one scrollable surface with the panes available beside it.
/// </summary>
public class OverviewDocumentViewModel : Document
{
	static readonly IBrush Added = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#3794FF"));
	static readonly IBrush Removed = new SolidColorBrush(Color.Parse("#F85149"));

	readonly ReviewWorkspace workspace;
	bool sweepRunning;

	public OverviewState State { get; } = new();
	public ObservableCollection<IssueRef> LinkedIssues { get; } = [];
	public ObservableCollection<CommitLine> CommitLines { get; } = [];
	public ObservableCollection<FileCostRow> FileCosts { get; } = [];
	public ObservableCollection<MemberRow> ImplMembers { get; } = [];
	public ObservableCollection<MemberRow> TestGroups { get; } = [];
	public ObservableCollection<CheckLine> CheckLines { get; } = [];
	public ObservableCollection<ReviewWorkspace.SweepItem> SweepItems { get; } = [];

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
		workspace.CoverageChanged += () => Dispatcher.UIThread.Post(() => {
			RebuildCoverage();
			// The sweep's uncovered-lines finding goes stale after a coverage run.
			RunSweepOnceAsync().HandleExceptions();
		});
		workspace.TestResultsChanged += () => Dispatcher.UIThread.Post(RebuildTests);
		workspace.StatusMessage += message => Dispatcher.UIThread.Post(() => State.ToolStatus = message);
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
			: workspace.HeadSha is null ? "No review open." : "Local range review";
		State.Description = workspace.CurrentPr?.Body is { Length: > 0 } body
			? body.ReplaceLineEndings("\n")
			: "(no description)";
		RebuildLinkedIssues();
		RebuildFiles();
		RebuildChecks();
		RebuildCoverage();
		RebuildTests();
		OnChangeMap();
		LoadCommitsAsync().HandleExceptions();
	}

	void RebuildLinkedIssues()
	{
		LinkedIssues.Clear();
		if (workspace.CurrentPr?.Body is not { } body)
			return;
		foreach (Match match in Regex.Matches(body, @"#(\d{2,6})\b").Take(12))
		{
			int number = int.Parse(match.Groups[1].Value);
			if (workspace.CurrentPr is { } pr && number == pr.Number)
				continue;
			if (LinkedIssues.All(i => i.Number != number))
				LinkedIssues.Add(new IssueRef($"#{number}", number));
		}
	}

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
		State.FilesHeader = totals.Rows.Count == 0 ? "" : $"{totals.Rows.Count} changed file(s), hot files (high churn) deserve extra caution:";
	}

	async Task LoadCommitsAsync()
	{
		CommitLines.Clear();
		State.CommitsHeader = "";
		if (workspace.BaseSha is not { } baseSha || workspace.HeadSha is not { } headSha)
			return;
		try
		{
			var commits = await workspace.Git.LogAsync($"{baseSha}..{headSha}", null, follow: false, limit: 20);
			var stats = await LoadCommitStatsAsync(baseSha, headSha);
			foreach (var commit in commits.Take(8))
			{
				var (added, removed) = stats.GetValueOrDefault(commit.Sha);
				CommitLines.Add(new CommitLine(commit.ShortSha, $"+{added}", $"-{removed}", commit.Subject));
			}
			if (commits.Count > 8)
				CommitLines.Add(new CommitLine("", "", "", $"... and {commits.Count - 8} more (Commits pane)"));
			State.CommitsHeader = commits.Count == 0 ? "" : $"{commits.Count} commit(s):";
		}
		catch (ToolFailedException)
		{
			// No commit summary is fine; the Commits pane still works.
		}
	}

	async Task<Dictionary<string, (int Added, int Removed)>> LoadCommitStatsAsync(string baseSha, string headSha)
	{
		var stats = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
		string output = await ExternalTool.RunAsync("git",
			["log", "--format=%H", "--shortstat", $"{baseSha}..{headSha}"], workspace.RepoPath);
		string? currentSha = null;
		foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
		{
			string trimmed = line.Trim();
			if (trimmed.Length == 40 && trimmed.All(char.IsAsciiHexDigit))
			{
				currentSha = trimmed;
			}
			else if (currentSha is not null && trimmed.Contains("changed", StringComparison.Ordinal))
			{
				var insertions = Regex.Match(trimmed, @"(\d+) insertion");
				var deletions = Regex.Match(trimmed, @"(\d+) deletion");
				stats[currentSha] = (
					insertions.Success ? int.Parse(insertions.Groups[1].Value) : 0,
					deletions.Success ? int.Parse(deletions.Groups[1].Value) : 0);
			}
		}
		return stats;
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
			? "Changed members: waiting for semantics..."
			: map.Count == 0
				? "Changed members: none (non-code changes only)."
				: $"{map.Count} changed member(s): {ImplMembers.Count} implementation (double-click to peek), tests grouped by type. Full tree: Map pane.";
		RunSweepOnceAsync().HandleExceptions();
	}

	async Task RunSweepOnceAsync()
	{
		if (sweepRunning || !workspace.ChangeMapComputed || workspace.HeadSha is null)
			return;
		sweepRunning = true;
		try
		{
			var items = await workspace.ComputeSweepAsync();
			SweepItems.Clear();
			foreach (var item in items)
				SweepItems.Add(item);
			State.SweepHeader = items.Count == 0
				? "Consequence sweep: no findings."
				: $"Consequence sweep - {items.Count} computed finding(s) (double-click to jump; prompts, not verdicts):";
		}
		finally
		{
			sweepRunning = false;
		}
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

	public void OpenSweepItem(ReviewWorkspace.SweepItem item)
	{
		if (item.Path is not null)
			workspace.NavigateToFileLineAsync(item.Path, Math.Max(1, item.Line), oldSide: false, record: true).HandleExceptions();
	}

	public void OpenPrOnGitHub()
	{
		if (workspace.CurrentPr is { } pr)
			workspace.OpenOnGitHubAsync(pr.Number).HandleExceptions();
	}

	public void OpenCommit(CommitLine line)
	{
		if (line.ShortSha.Length > 0)
			workspace.OpenCommitOnGitHubAsync(line.ShortSha).HandleExceptions();
	}

	public void OpenCheck(CheckLine line)
	{
		if (line.Link is { Length: > 0 } url)
			workspace.OpenUrlAsync(url).HandleExceptions();
	}

	public void Bounce() => workspace.PrepareBounceBody();

	public void OpenRecord() => workspace.OpenReviewRecord();
}
