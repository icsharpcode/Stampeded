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

public sealed partial class PrepareItem(string label) : ObservableObject
{
	public string Label { get; } = label;

	[ObservableProperty]
	string status = "waiting";

	[ObservableProperty]
	bool done;
}

/// <summary>A local branch on the start page, annotated with its associated PR when one
/// exists - including whether the local head differs from what the PR is showing.</summary>
public sealed record BranchRow(BranchInfo Info, string PrTag)
{
	public bool HasPrTag => PrTag.Length > 0;
}

public sealed record TriageRowDisplay(string Marker, string Path, string AddedText, string RemovedText, string MinutesText, string Churn);

public sealed partial class WizardState : ObservableObject
{
	[ObservableProperty]
	string prColumnHeader = "Pull Requests";

	[ObservableProperty]
	string commitsSummary = "";

	[ObservableProperty]
	string branchColumnHeader = "Branches";

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
	public ObservableCollection<PrepareItem> PrepareItems { get; } = [];
	public WizardState State { get; } = new();

	// Start-page data.
	public ObservableCollection<string> Recents { get; } = new(RecentRepos.Load());
	public ObservableCollection<BranchRow> Branches { get; } = [];
	IReadOnlyList<BranchInfo> rawBranches = [];
	public PrListPaneViewModel PrList { get; }

	// Inline content of the phase steps.
	public ChangeMapPaneViewModel Map { get; }
	public PrFilesPaneViewModel FilesList { get; }

	public WizardStep SelectStep { get; }
	public WizardStep PrepareStep { get; }

	public WizardStep TriageStep { get; }
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
		Map = new ChangeMapPaneViewModel(workspace);
		FilesList = new PrFilesPaneViewModel(workspace);

		Steps.Add(SelectStep = new WizardStep("select", "Start",
			"Pick what to review: a pull request, a local branch, or another repository. 'Open Guided' walks the phases; 'Open Plain' just opens the review.", false));
		Steps.Add(PrepareStep = new WizardStep("prepare", "Prep",
			"The workspace is being prepared: worktrees, semantics for both sides, CI state, hot-spot data. The review starts when the signals the phases rely on are in - or continue now and let the rest catch up.", false));
		foreach (var label in new[] {
			"Diff and review state",
			"Semantics (head)",
			"Semantics (base, for removed code)",
			"Change map (symbol-level diff)",
			"CI checks",
			"Churn / hot spots",
			"Posted review comments",
		})
		{
			PrepareItems.Add(new PrepareItem(label));
		}
		Steps.Add(TriageStep = new WizardStep("triage", "1 Assess",
			"Intent and cost on one screen: what is this supposed to do (description, commits), and should you review it now, at all (weighted estimate, CI, hot spots)? Bouncing an unreviewable PR is a legitimate outcome - the button drafts the comment. No diff yet.", true));
		Steps.Add(SurveyStep = new WizardStep("survey", "2 Survey",
			"Scan the whole change at symbol level before reading any of it closely: where is the center of gravity? Two minutes, no depth. Click a member to peek.", true));
		Steps.Add(PlanStep = new WizardStep("plan", "3 Plan",
			"Decide depth per file (right-click: deep / skim / trust). Stop pretending you will read every line evenly - allocate deliberately.", true));
		Steps.Add(TraverseStep = new WizardStep("traverse", "4 Traverse",
			"The deep read happens in the diff tabs: n/p hunks, v viewed, F12/Shift+F12 to verify claims instead of believing them. This page keeps the session honest.", false));
		Steps.Add(SweepStep = new WizardStep("sweep", "5 Sweep",
			"The delocalized pass - different machinery than reading: surviving callers of removed code, dependency changes, changes without test changes. Computed below; double-click to jump.", true));
		Steps.Add(CloseStep = new WizardStep("close", "6 Close",
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
			LoadCommitsSummaryAsync().HandleExceptions();
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
		workspace.ChurnChanged += () => Dispatcher.UIThread.Post(Recompute);
		workspace.SemanticsChanged += () => Dispatcher.UIThread.Post(Recompute);
		workspace.ApprovalGate = Gate;
		sessionTimer.Tick += (_, _) => Recompute();
		PrList.Items.CollectionChanged += (_, _) => {
			State.PrColumnHeader = $"Pull Requests ({PrList.Items.Count})";
			AnnotateBranches();
		};
		SelectCurrent(SelectStep);
		Recompute();
		LoadStartPageAsync().HandleExceptions();
	}

	async Task LoadCommitsSummaryAsync()
	{
		State.CommitsSummary = "";
		if (workspace.BaseSha is not { } baseSha || workspace.HeadSha is not { } headSha)
			return;
		try
		{
			var commits = await workspace.Git.LogAsync($"{baseSha}..{headSha}", null, follow: false, limit: 20);
			State.CommitsSummary = commits.Count == 0 ? "" :
				$"{commits.Count} commit(s):\n" + string.Join("\n",
					commits.Take(8).Select(c => $"  {c.ShortSha}  {c.Subject}"))
				+ (commits.Count > 8 ? $"\n  ... and {commits.Count - 8} more (Commits pane)" : "");
		}
		catch (ToolFailedException)
		{
			// No commit summary is fine; the Commits pane still works.
		}
	}

	async Task LoadStartPageAsync()
	{
		try
		{
			defaultBase = await workspace.Git.GetDefaultBaseAsync();
			rawBranches = await workspace.Git.ListBranchesAsync();
			AnnotateBranches();
		}
		catch (ToolFailedException)
		{
			// Not a repo or no origin; the start page simply shows no branches.
		}
		State.BranchColumnHeader = $"Branches ({Branches.Count})";
	}

	/// <summary>Rebuilds branch rows with their PR association. A branch whose PR exists
	/// is opened via the PR normally; a "(differs)" tag warns that the local head is not
	/// what the PR shows (local-only commits are only reviewable as a branch).</summary>
	void AnnotateBranches()
	{
		var prsByBranch = PrList.Items
			.GroupBy(p => p.HeadRefName, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
		var rows = new List<BranchRow>();
		foreach (var branch in rawBranches)
		{
			string tag = "";
			if (prsByBranch.TryGetValue(branch.Name, out var pr))
			{
				bool differs = pr.HeadRefOid is { Length: > 0 } oid
					&& !string.Equals(oid, branch.Sha, StringComparison.OrdinalIgnoreCase);
				tag = differs ? $"PR #{pr.Number} (differs)" : $"PR #{pr.Number}";
			}
			rows.Add(new BranchRow(branch, tag));
		}
		Branches.Clear();
		// PR-backed branches first (the ones most likely under review), recency preserved
		// within each group.
		foreach (var row in rows.OrderBy(r => r.HasPrTag ? 0 : 1))
			Branches.Add(row);
	}

	void SelectCurrent(WizardStep step)
	{
		foreach (var s in Steps)
			s.IsCurrent = s == step;
		Title = step == SelectStep ? "Wizard" : $"Wizard - {step.Title}";
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

	bool autoAdvanceAfterPrepare;

	public void OpenPr(PrSummary pr, bool guided)
	{
		workspace.OpenPrAsync(pr.Number, guided).HandleExceptions();
		if (guided)
			BeginGuidedPreparation();
	}

	public void OpenBranch(BranchInfo branch, bool guided)
	{
		workspace.OpenLocalRangeAsync(defaultBase, branch.Name, guided).HandleExceptions();
		if (guided)
			BeginGuidedPreparation();
	}

	/// <summary>Guided open: show the preparation checklist and advance to Triage once the
	/// signals the phases rely on are loaded.</summary>
	public void BeginGuidedPreparation()
	{
		autoAdvanceAfterPrepare = true;
		SelectStepCommand(PrepareStep);
	}

	public void ContinueFromPrepare()
	{
		autoAdvanceAfterPrepare = false;
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
				marker, row.Path, $"+{row.Added}", $"-{row.Removed}", $"~{row.Minutes} min",
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

	static (string Status, bool Done) SemanticStatus(Core.Roslyn.RoslynWorkspaceService? sem)
		=> sem?.State switch {
			Core.Roslyn.SemanticState.Restoring => ("restoring packages...", false),
			Core.Roslyn.SemanticState.Loading => ("loading solution...", false),
			Core.Roslyn.SemanticState.Ready => ("ready", true),
			Core.Roslyn.SemanticState.SyntaxOnly => ("syntax-only fallback", true),
			Core.Roslyn.SemanticState.Failed => ("FAILED (see load log)", true),
			_ => ("waiting", false),
		};

	void UpdatePrepareItems()
	{
		bool reviewOpen = workspace.HeadSha is not null;
		Set(0, reviewOpen ? ($"{workspace.Files.Count} changed file(s)", true) : ("waiting", false));
		Set(1, SemanticStatus(workspace.Semantics));
		Set(2, SemanticStatus(workspace.BaseSemantics));
		// A computed-but-empty map is done (non-C# changes have no members); a FAILED
		// semantic load means the map will never come - do not wait for it forever.
		Set(3, workspace.ChangeMapComputed
			? ($"{workspace.ChangeMap.Count} member(s)", true)
			: workspace.Semantics is { State: Core.Roslyn.SemanticState.Failed }
				? ("unavailable (semantics failed)", true)
				: ("waiting for semantics...", false));
		Set(4, workspace.Checks is { } checks ? ($"{checks.Count} check(s)", true)
			: workspace.CurrentPr is null && reviewOpen ? ("local review - none", true)
			: ("loading...", false));
		Set(5, workspace.ChurnByFile is not null ? ("done", true) : ("computing...", false));
		Set(6, !reviewOpen ? ("waiting", false)
			: workspace.CommentsLoaded ? ($"{workspace.PostedComments.Count} comment(s)", true)
			: ("loading...", false));

		// The phases' load-bearing inputs: both semantic sides terminal, CI and churn in.
		// The map and comments trail behind harmlessly (and may legitimately stay empty).
		bool ready = reviewOpen && PrepareItems[1].Done && PrepareItems[2].Done
			&& PrepareItems[4].Done && PrepareItems[5].Done;
		PrepareStep.AutoConditionMet = ready && PrepareItems[3].Done && PrepareItems[6].Done;
		if (ready && autoAdvanceAfterPrepare && PrepareStep.IsCurrent)
		{
			autoAdvanceAfterPrepare = false;
			SelectStepCommand(TriageStep);
		}

		void Set(int index, (string Status, bool Done) value)
		{
			PrepareItems[index].Status = value.Status;
			PrepareItems[index].Done = value.Done;
		}
	}

	void Recompute()
	{
		UpdatePrepareItems();
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
					string title = workspace.CurrentPr is { } pr ? $"#{pr.Number} {pr.Title}\n" : "";
					step.Facts = total == 0 ? "Open a review first (Start step)." : title +
						$"Weighted estimate: ~{t.Minutes} min = {t.Sittings} sitting(s). " +
						$"Implementation {t.ImplChanged} line(s) @5/min, tests {t.TestChanged} @15/min, " +
						$"generated {t.GeneratedChanged} @50/min, {t.DependencyFiles} manifest file(s) flat.\n" +
						ci + " Per-file cost and churn below - hot files (high churn) deserve extra caution." + rereview;
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
		int gated = Steps.Count - 2;
		State.Progress = $"{Steps.Count(s => s != SelectStep && s != PrepareStep && s.IsSatisfied)} of {gated} phases complete{(State.OverrideGate ? "  (gate overridden)" : "")}";
	}

	(bool Ok, string Detail) Gate()
	{
		if (State.OverrideGate)
			return (true, "");
		var unmet = Steps.Where(s => s != SelectStep && s != PrepareStep && !s.IsSatisfied).Select(s => s.Title).ToList();
		return unmet.Count == 0
			? (true, "")
			: (false, string.Join("; ", unmet));
	}
}
