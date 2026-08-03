using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

namespace Stampeded.Panes;

public sealed partial class GuideStage(string id, string title, string guidance, bool requiresCheck) : ObservableObject
{
	public string Id { get; } = id;
	public string Title { get; } = title;
	public string Guidance { get; } = guidance;
	public bool RequiresCheck { get; } = requiresCheck;

	[ObservableProperty]
	string autoFact = "";

	[ObservableProperty]
	bool isChecked;

	[ObservableProperty]
	bool isSatisfied;

	[ObservableProperty]
	bool autoConditionMet = true;
}

public sealed partial class GuideState : ObservableObject
{
	[ObservableProperty]
	string progress = "";

	[ObservableProperty]
	bool overrideGate;
}

/// <summary>
/// A staged review workflow so nothing gets lost: understand intent first, judge design,
/// line-pass every file, verify tests/coverage, check CI evidence, wrap up drafts.
/// Approval is gated on completing the stages (override available). Grounded in code
/// review research: understanding is the bottleneck (Bacchelli/Bird 2013), ordered
/// reading reduces cognitive load (Baum 2019), checklists catch omissions (Cisco study),
/// design-first staging per Google's engineering practices.
/// </summary>
public class GuidePaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	bool loadingChecks;

	public ObservableCollection<GuideStage> Stages { get; } = [];
	public GuideState State { get; } = new();

	public GuidePaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		Stages.Add(new GuideStage("intent", "1. Understand the intent",
			"Read the PR overview tab (opened automatically): what is this change supposed to do, and why? Don't hunt lines before you can answer that.", true));
		Stages.Add(new GuideStage("design", "2. Judge the design first",
			"Start at the main part of the change, not the first file: does the approach fit the codebase? Would something simpler do?", true));
		Stages.Add(new GuideStage("linepass", "3. Line pass over every file",
			"Walk every hunk (n/p) in every file - implementation files are listed before tests - and mark each file viewed (v). Use F12/Shift+F12 to verify callers of changed code.", false));
		Stages.Add(new GuideStage("tests", "4. Tests and coverage",
			"Is the new behavior tested? Run the relevant tests (Tests pane), use Run + Coverage and look for red strips on added lines.", true));
		Stages.Add(new GuideStage("evidence", "5. CI evidence",
			"Check the Checks pane: is CI green, and did the jobs that matter actually run?", true));
		Stages.Add(new GuideStage("wrapup", "6. Wrap up",
			"Every draft comment is either submitted or deliberately dropped; nothing you meant to say is still pending.", true));

		foreach (var stage in Stages)
			stage.PropertyChanged += (_, e) => {
				if (e.PropertyName == nameof(GuideStage.IsChecked) && !loadingChecks)
				{
					workspace.Store.SetGuideCheck(stage.Id, stage.IsChecked);
					Recompute();
				}
			};
		State.PropertyChanged += (_, _) => Recompute();

		workspace.ReviewChanged += () => { LoadChecks(); Recompute(); };
		workspace.ViewedChanged += (_, _) => Recompute();
		workspace.CommentsChanged += Recompute;
		workspace.CoverageChanged += Recompute;
		workspace.ChecksLoaded += Recompute;
		workspace.ApprovalGate = Gate;
	}

	void LoadChecks()
	{
		loadingChecks = true;
		foreach (var stage in Stages)
			stage.IsChecked = workspace.Store.GetGuideCheck(stage.Id);
		loadingChecks = false;
	}

	void Recompute()
	{
		int total = workspace.Files.Count;
		int viewed = workspace.Files.Count(f => workspace.Store.IsViewed(f.Path));
		foreach (var stage in Stages)
		{
			switch (stage.Id)
			{
				case "intent":
					stage.AutoFact = workspace.CurrentPr is { } pr ? $"Reviewing #{pr.Number}: {pr.Title}" : "";
					break;
				case "design":
					var main = workspace.Files
						.Where(f => !IsTestPath(f.Path))
						.OrderByDescending(f => f.Hunks.Sum(h => h.Lines.Count))
						.FirstOrDefault();
					stage.AutoFact = main is null ? "" : $"Largest non-test change: {main.Path}";
					break;
				case "linepass":
					stage.AutoConditionMet = total > 0 && viewed == total;
					stage.AutoFact = total == 0 ? "No files." : $"{viewed} of {total} file(s) viewed.";
					break;
				case "tests":
					var (uncovered, measured) = workspace.UncoveredAddedLines();
					stage.AutoFact = workspace.Coverage is null
						? "No coverage run yet (Tests pane > Run + Coverage)."
						: $"{uncovered} uncovered of {measured} measured added line(s).";
					break;
				case "evidence":
					var checks = workspace.Checks;
					int failing = checks?.Count(c => c.Bucket == "fail") ?? 0;
					stage.AutoFact = checks is null
						? "Checks not loaded yet."
						: failing > 0 ? $"{failing} of {checks.Count} check(s) FAILING." : $"All {checks.Count} check(s) passing or skipped.";
					break;
				case "wrapup":
					int drafts = workspace.Drafts.Count;
					stage.AutoFact = drafts == 0 ? "No pending drafts." : $"{drafts} draft(s) pending submission.";
					break;
			}
			stage.IsSatisfied = stage.AutoConditionMet && (!stage.RequiresCheck || stage.IsChecked);
		}
		int done = Stages.Count(s => s.IsSatisfied);
		State.Progress = $"{done} of {Stages.Count} stages complete{(State.OverrideGate ? "  (gate overridden)" : "")}";
	}

	(bool Ok, string Detail) Gate()
	{
		if (State.OverrideGate)
			return (true, "");
		var unmet = Stages.Where(s => !s.IsSatisfied).Select(s => s.Title).ToList();
		return unmet.Count == 0
			? (true, "")
			: (false, string.Join("; ", unmet));
	}

	internal static bool IsTestPath(string path)
		=> path.Contains("test", StringComparison.OrdinalIgnoreCase);
}
