using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Git;
using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;
using Stampeded.Panes;

namespace Stampeded.Documents;

public sealed partial class WizardStep(string id, string title, string guidance, bool requiresCheck) : ObservableObject
{
	public string Id { get; } = id;
	public string Title { get; } = title;
	public string Guidance { get; } = guidance;
	public bool RequiresCheck { get; } = requiresCheck;

	[ObservableProperty]
	string facts = "";

	[ObservableProperty]
	bool isChecked;

	[ObservableProperty]
	bool isSatisfied;

	[ObservableProperty]
	bool autoConditionMet = true;

	[ObservableProperty]
	bool isCurrent;
}

public sealed record TriageRowDisplay(string Marker, string Path, string Delta, string MinutesText, string Churn);

public sealed partial class WizardState : ObservableObject
{
	[ObservableProperty]
	string progress = "";

	[ObservableProperty]
	bool overrideGate;

	[ObservableProperty]
	string description = "";
}

/// <summary>
/// The review method as a wizard document: a start page (repos, PRs, branches), then the
/// phases of a review - triage, orient, survey, plan, traverse, sweep, close - each with
/// its content inline (description, commits, map, files) instead of driving other panes.
/// A review can also be opened plain, without the wizard flow. Approval stays gated on
/// the phases (override available).
/// </summary>
public class WizardViewModel : Document
{
	readonly ReviewWorkspace workspace;
	readonly DispatcherTimer sessionTimer = new() { Interval = TimeSpan.FromSeconds(30) };
	bool loadingChecks;
	DateTimeOffset? traverseStarted;
	bool breakSuggested;

	public ObservableCollection<WizardStep> Steps { get; } = [];
	public ObservableCollection<ReviewWorkspace.SweepItem> SweepItems { get; } = [];
	public ObservableCollection<TriageRowDisplay> TriageRows { get; } = [];
	public WizardState State { get; } = new();

	// Start-page data.
	public ObservableCollection<string> Recents { get; } = new(RecentRepos.Load());
	public ObservableCollection<BranchInfo> Branches { get; } = [];
	public PrListPaneViewModel PrList { get; }

	// Inline content of the phase steps.
	public CommitsPaneViewModel Commits { get; }
	public ChangeMapPaneViewModel Map { get; }
	public PrFilesPaneViewModel FilesList { get; }

	public WizardStep SelectStep { get; }
	public WizardStep TriageStep { get; }
	public WizardStep OrientStep { get; }
	public WizardStep SurveyStep { get; }
	public WizardStep PlanStep { get; }
	public WizardStep TraverseStep { get; }
	public WizardStep SweepStep { get; }
	public WizardStep CloseStep { get; }

	string defaultBase = "origin/master";

	public WizardViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		Title = "Review Wizard";
		PrList = new PrListPaneViewModel(workspace);
		Commits = new CommitsPaneViewModel(workspace);
		Map = new ChangeMapPaneViewModel(workspace);
		FilesList = new PrFilesPaneViewModel(workspace);

		Steps.Add(SelectStep = new WizardStep("select", "Start",
			"Pick what to review: a pull request, a local branch, or another repository. 'Open Guided' walks the phases; 'Open Plain' just opens the review.", false));
		Steps.Add(TriageStep = new WizardStep("triage", "1 Triage",
			"Should you review this, now, at all? Read the cost estimate. Bouncing an unreviewable PR is a legitimate outcome - the button drafts the comment.", true));
		Steps.Add(OrientStep = new WizardStep("orient", "2 Orient",
			"Establish INTENT before reading any code: description and commits below. What is this supposed to do, by what approach? No diff yet.", true));
		Steps.Add(SurveyStep = new WizardStep("survey", "3 Survey",
			"Scan the whole change at symbol level before reading any of it closely: where is the center of gravity? Two minutes, no depth. Click a member to peek.", true));
		Steps.Add(PlanStep = new WizardStep("plan", "4 Plan",
			"Decide depth per file (right-click: deep / skim / trust). Stop pretending you will read every line evenly - allocate deliberately.", true));
		Steps.Add(TraverseStep = new WizardStep("traverse", "5 Traverse",
			"The deep read happens in the diff tabs: n/p hunks, v viewed, F12/Shift+F12 to verify claims instead of believing them. This page keeps the session honest.", false));
		Steps.Add(SweepStep = new WizardStep("sweep", "6 Sweep",
			"The delocalized pass - different machinery than reading: surviving callers of removed code, dependency changes, changes without test changes. Computed below; double-click to jump.", true));
		Steps.Add(CloseStep = new WizardStep("close", "7 Close",
			"Record what you concluded: reviewed at which depth, verified how, deliberately skipped what. Then submit or drop every draft (Comments pane).", true));

		foreach (var step in Steps)
			step.PropertyChanged += (_, e) => {
				if (e.PropertyName == nameof(WizardStep.IsChecked) && !loadingChecks)
				{
					workspace.Store.SetGuideCheck(step.Id, step.IsChecked);
					Recompute();
				}
			};
		State.PropertyChanged += (_, _) => Recompute();

		workspace.ReviewChanged += () => {
			LoadChecks();
			traverseStarted = null;
			breakSuggested = false;
			SweepItems.Clear();
			State.Description = workspace.CurrentPr?.Body is { Length: > 0 } body
				? body.ReplaceLineEndings("\n")
				: "(no description)";
			RebuildTriageRows();
			Recompute();
		};
		workspace.ViewedChanged += (_, _) => Recompute();
		workspace.CommentsChanged += Recompute;
		workspace.CoverageChanged += Recompute;
		workspace.ChecksLoaded += Recompute;
		workspace.ChangeMapChanged += Recompute;
		workspace.DepthChanged += Recompute;
		workspace.ChurnChanged += () => Dispatcher.UIThread.Post(RebuildTriageRows);
		workspace.ApprovalGate = Gate;
		sessionTimer.Tick += (_, _) => Recompute();
		SelectCurrent(SelectStep);
		Recompute();
		LoadStartPageAsync().HandleExceptions();
	}

	async Task LoadStartPageAsync()
	{
		try
		{
			defaultBase = await workspace.Git.GetDefaultBaseAsync();
			var branches = await workspace.Git.ListBranchesAsync();
			Branches.Clear();
			foreach (var branch in branches)
				Branches.Add(branch);
		}
		catch (ToolFailedException)
		{
			// Not a repo or no origin; the start page simply shows no branches.
		}
	}

	void SelectCurrent(WizardStep step)
	{
		foreach (var s in Steps)
			s.IsCurrent = s == step;
	}

	public WizardStep Current => Steps.First(s => s.IsCurrent);

	public void SelectStepCommand(WizardStep step)
	{
		SelectCurrent(step);
		if (step == TraverseStep)
		{
			traverseStarted ??= DateTimeOffset.Now;
			sessionTimer.Start();
		}
		else
		{
			sessionTimer.Stop();
		}
		if (step == SweepStep)
			RunSweepAsync().HandleExceptions();
		Recompute();
	}

	public void NextStep()
	{
		// Next is the completion gesture: advancing past a phase marks it done. Skip (and
		// the step chips) move on without committing.
		if (Current.RequiresCheck)
			Current.IsChecked = true;
		Advance();
	}

	public void SkipStep() => Advance();

	void Advance()
	{
		int i = Steps.IndexOf(Current);
		if (i < Steps.Count - 1)
			SelectStepCommand(Steps[i + 1]);
	}

	public void PreviousStep()
	{
		int i = Steps.IndexOf(Current);
		if (i > 0)
			SelectStepCommand(Steps[i - 1]);
	}

	// Start-page actions.

	public void OpenPr(PrSummary pr, bool guided)
	{
		workspace.OpenPrAsync(pr.Number, guided).HandleExceptions();
		if (guided)
			SelectStepCommand(TriageStep);
	}

	public void OpenBranch(BranchInfo branch, bool guided)
	{
		workspace.OpenLocalRangeAsync(defaultBase, branch.Name, guided).HandleExceptions();
		if (guided)
			SelectStepCommand(TriageStep);
	}

	public void OpenRecent(string path) => App.OpenRepositoryAsync(path).HandleExceptions();

	public void Bounce() => workspace.PrepareBounceBody();

	public void OpenRecord() => workspace.OpenReviewRecord();

	public void OpenSweepItem(ReviewWorkspace.SweepItem item)
	{
		if (item.Path is not null)
			workspace.NavigateToFileLineAsync(item.Path, Math.Max(1, item.Line), oldSide: false, record: true).HandleExceptions();
	}

	async Task RunSweepAsync()
	{
		var items = await workspace.ComputeSweepAsync();
		SweepItems.Clear();
		foreach (var item in items)
			SweepItems.Add(item);
		Recompute();
	}

	void RebuildTriageRows()
	{
		TriageRows.Clear();
		var totals = workspace.ComputeTriage();
		foreach (var row in totals.Rows)
		{
			string marker = row.Category switch {
				Core.Review.FileCategory.Test => "test",
				Core.Review.FileCategory.Dependency => "deps",
				Core.Review.FileCategory.Generated => "gen",
				_ => "impl",
			};
			int churn = workspace.ChurnByFile?.GetValueOrDefault(row.Path) ?? 0;
			TriageRows.Add(new TriageRowDisplay(
				marker, row.Path, $"+{row.Added} -{row.Removed}", $"~{row.Minutes} min",
				churn > 0 ? $"{churn}x/yr" : ""));
		}
	}

	void LoadChecks()
	{
		loadingChecks = true;
		foreach (var step in Steps)
			step.IsChecked = workspace.Store.GetGuideCheck(step.Id);
		loadingChecks = false;
	}

	void Recompute()
	{
		int total = workspace.Files.Count;
		foreach (var step in Steps)
		{
			switch (step.Id)
			{
				case "select":
					step.Facts = workspace.CurrentPr is { } open
						? $"Reviewing #{open.Number}: {open.Title}"
						: workspace.HeadSha is null ? "" : "Reviewing a local range.";
					break;
				case "triage":
					var t = workspace.ComputeTriage();
					string rereview = workspace.TouchedSinceLastPass is not null
						? $"\nRE-REVIEW: only {workspace.Files.Count(f => workspace.IsTouchedSinceLastPass(f.Path))} of {total} file(s) changed since your last pass (marked 'new!'); earlier viewed flags carried over."
						: "";
					var checks = workspace.Checks;
					string ci = checks is null
						? "CI: not loaded yet."
						: checks.Count(c => c.Bucket == "fail") is var failing && failing > 0
							? $"CI: {failing} of {checks.Count} check(s) FAILING - is this ready for review?"
							: $"CI: all {checks.Count} check(s) passing or skipped.";
					step.Facts = total == 0 ? "Open a review first (Start step)." :
						$"Weighted estimate: ~{t.Minutes} min = {t.Sittings} sitting(s). " +
						$"Implementation {t.ImplChanged} line(s) @5/min, tests {t.TestChanged} @15/min, " +
						$"generated {t.GeneratedChanged} @50/min, {t.DependencyFiles} manifest file(s) flat.\n" +
						ci + " Per-file cost and churn below - hot files (high churn) deserve extra caution." + rereview;
					break;
				case "orient":
					step.Facts = workspace.CurrentPr is { } pr ? $"#{pr.Number} {pr.Title}" : "";
					break;
				case "survey":
					step.Facts = workspace.ChangeMap.Count > 0
						? $"{workspace.ChangeMap.Count} changed member(s): green added, blue modified, red removed."
						: "Map not ready yet (semantics still loading).";
					break;
				case "plan":
					int planned = workspace.Files.Count(f => workspace.GetDepth(f.Path) != "");
					int deep = workspace.Files.Count(f => workspace.GetDepth(f.Path) == "deep");
					step.AutoConditionMet = total == 0 || planned == total;
					step.Facts = total == 0 ? "" : $"{planned} of {total} file(s) given a depth ({deep} deep).";
					break;
				case "traverse":
					var inScope = workspace.Files.Where(f => workspace.GetDepth(f.Path) != "trust").ToList();
					int scopeViewed = inScope.Count(f => workspace.Store.IsViewed(f.Path));
					step.AutoConditionMet = total > 0 && scopeViewed == inScope.Count;
					string session = "";
					if (traverseStarted is { } started)
					{
						int minutes = (int)(DateTimeOffset.Now - started).TotalMinutes;
						int linesViewed = workspace.Files
							.Where(f => workspace.Store.IsViewed(f.Path))
							.Sum(f => workspace.AddedLineCount(f.Path));
						session = $"\nSession: {minutes} min, ~{linesViewed} added line(s) covered.";
						if (!breakSuggested && (minutes >= 90 || linesViewed >= 400))
						{
							breakSuggested = true;
							session += " Past the 400-line/90-min band - defect discovery degrades from here; consider a break.";
						}
					}
					step.Facts = $"{scopeViewed} of {inScope.Count} in-scope file(s) viewed.{session}";
					break;
				case "sweep":
					step.Facts = SweepItems.Count == 0
						? "No findings (the sweep runs when you enter this step)."
						: $"{SweepItems.Count} computed finding(s).";
					break;
				case "close":
					int drafts = workspace.Drafts.Count;
					step.Facts = (drafts == 0 ? "No pending drafts." : $"{drafts} draft(s) pending.")
						+ " 'Review record' writes the close-out artifact.";
					break;
			}
			step.IsSatisfied = step.AutoConditionMet && (!step.RequiresCheck || step.IsChecked);
		}
		int gated = Steps.Count - 1;
		State.Progress = $"{Steps.Count(s => s != SelectStep && s.IsSatisfied)} of {gated} phases complete{(State.OverrideGate ? "  (gate overridden)" : "")}";
	}

	(bool Ok, string Detail) Gate()
	{
		if (State.OverrideGate)
			return (true, "");
		var unmet = Steps.Where(s => s != SelectStep && !s.IsSatisfied).Select(s => s.Title).ToList();
		return unmet.Count == 0
			? (true, "")
			: (false, string.Join("; ", unmet));
	}
}
