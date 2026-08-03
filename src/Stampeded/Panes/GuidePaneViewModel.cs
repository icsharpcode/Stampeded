using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Panes;

public sealed partial class GuidePhase(string id, string title, string guidance, bool requiresCheck) : ObservableObject
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

public sealed partial class GuideState : ObservableObject
{
	[ObservableProperty]
	string progress = "";

	[ObservableProperty]
	bool overrideGate;

	[ObservableProperty]
	bool isSweep;

	[ObservableProperty]
	bool isTriage;

	[ObservableProperty]
	bool isClose;
}

/// <summary>
/// The review method as modes: a review is a hypothesis (intent, mechanism, consequence)
/// and each phase builds or attacks a different part of it. Selecting a phase switches
/// the surrounding layout - the surfaces follow the mode. Approval is gated on the
/// phases (override available).
/// </summary>
public class GuidePaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	readonly DispatcherTimer sessionTimer = new() { Interval = TimeSpan.FromSeconds(30) };
	bool loadingChecks;
	DateTimeOffset? traverseStarted;
	bool breakSuggested;

	public ObservableCollection<GuidePhase> Phases { get; } = [];
	public ObservableCollection<ReviewWorkspace.SweepItem> SweepItems { get; } = [];
	public GuideState State { get; } = new();

	/// <summary>Raised when a phase wants a pane brought up; the dock factory routes it.</summary>
	public event Action<string>? PaneRequested;

	GuidePhase? current;
	public GuidePhase? Current {
		get => current;
		private set {
			if (current is not null)
				current.IsCurrent = false;
			current = value;
			if (current is not null)
				current.IsCurrent = true;
			State.IsSweep = current?.Id == "sweep";
			State.IsTriage = current?.Id == "triage";
			State.IsClose = current?.Id == "close";
		}
	}

	public GuidePaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		Phases.Add(new GuidePhase("triage", "1 Triage",
			"Should you review this, now, at all? Read the cost estimate below. Bouncing an unreviewable PR is a legitimate outcome - the button drafts the comment.", true));
		Phases.Add(new GuidePhase("orient", "2 Orient",
			"Establish INTENT before reading any code: the overview tab and the commit list. What is this supposed to do, by what approach? No diff on screen yet.", true));
		Phases.Add(new GuidePhase("survey", "3 Survey",
			"Scan the whole change in the Map before reading any of it closely: what changed at symbol level, where is the center of gravity? Two minutes, no depth.", true));
		Phases.Add(new GuidePhase("plan", "4 Plan",
			"Decide depth per file in the Files pane (right-click: deep / skim / trust). Stop pretending you will read every line evenly - allocate deliberately.", true));
		Phases.Add(new GuidePhase("traverse", "5 Traverse",
			"The deep read, in dependency order within each area. At every claim: believe it or CHECK it (F12, Shift+F12, run the test) - cheap verification beats judgment under load. Mark files viewed (v).", false));
		Phases.Add(new GuidePhase("sweep", "6 Sweep",
			"The delocalized pass - different machinery than reading: surviving callers of removed code, dependency changes, behavior changes without test changes. Answered mechanically below; double-click to jump.", true));
		Phases.Add(new GuidePhase("close", "7 Close",
			"Record what you concluded: what you reviewed at which depth, what you verified by executing, and what you deliberately did not look at. Then submit or drop every draft.", true));

		foreach (var phase in Phases)
			phase.PropertyChanged += (_, e) => {
				if (e.PropertyName == nameof(GuidePhase.IsChecked) && !loadingChecks)
				{
					workspace.Store.SetGuideCheck(phase.Id, phase.IsChecked);
					Recompute();
				}
			};
		State.PropertyChanged += (_, _) => Recompute();

		workspace.ReviewChanged += () => {
			LoadChecks();
			traverseStarted = null;
			breakSuggested = false;
			SweepItems.Clear();
			SelectPhase(Phases[0], activateLayout: false);
			Recompute();
		};
		workspace.ViewedChanged += (_, _) => Recompute();
		workspace.CommentsChanged += Recompute;
		workspace.CoverageChanged += Recompute;
		workspace.ChecksLoaded += Recompute;
		workspace.ChangeMapChanged += Recompute;
		workspace.DepthChanged += Recompute;
		workspace.ApprovalGate = Gate;
		sessionTimer.Tick += (_, _) => Recompute();
		Current = Phases[0];
	}

	public void SelectPhase(GuidePhase phase) => SelectPhase(phase, activateLayout: true);

	void SelectPhase(GuidePhase phase, bool activateLayout)
	{
		Current = phase;
		if (phase.Id == "traverse")
		{
			traverseStarted ??= DateTimeOffset.Now;
			sessionTimer.Start();
		}
		else
		{
			sessionTimer.Stop();
		}
		if (activateLayout)
			ActivateLayout(phase.Id);
		if (phase.Id == "sweep")
			RunSweepAsync().HandleExceptions();
		Recompute();
	}

	void ActivateLayout(string id)
	{
		switch (id)
		{
			case "triage":
			case "plan":
			case "traverse":
				PaneRequested?.Invoke("Files");
				break;
			case "orient":
				workspace.OpenOverviewDocument();
				PaneRequested?.Invoke("Commits");
				break;
			case "survey":
				PaneRequested?.Invoke("Map");
				break;
			case "close":
				PaneRequested?.Invoke("Comments");
				break;
		}
	}

	public void NextPhase()
	{
		int i = Current is null ? 0 : Phases.IndexOf(Current);
		if (i < Phases.Count - 1)
			SelectPhase(Phases[i + 1]);
	}

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

	void LoadChecks()
	{
		loadingChecks = true;
		foreach (var phase in Phases)
			phase.IsChecked = workspace.Store.GetGuideCheck(phase.Id);
		loadingChecks = false;
	}

	void Recompute()
	{
		int total = workspace.Files.Count;
		int viewed = workspace.Files.Count(f => workspace.Store.IsViewed(f.Path));
		foreach (var phase in Phases)
		{
			switch (phase.Id)
			{
				case "triage":
					var t = workspace.ComputeTriage();
					phase.Facts = total == 0 ? "" :
						$"{t.FileCount} file(s) in {t.ProjectCount} project(s): +{t.Added} -{t.Removed}.\n" +
						$"Estimated: ~{t.EstimatedMinutes} min = {t.EstimatedSittings} sitting(s) of focused review.\n" +
						$"{t.TestFileCount} test file(s) touched; {t.DependencyFileCount} dependency/manifest file(s).";
					break;
				case "orient":
					phase.Facts = workspace.CurrentPr is { } pr ? $"Reviewing #{pr.Number}: {pr.Title}" : "";
					break;
				case "survey":
					phase.Facts = workspace.ChangeMap.Count > 0
						? $"{workspace.ChangeMap.Count} changed member(s) in the Map (green added, blue modified, red removed)."
						: "Map not ready yet (semantics still loading).";
					break;
				case "plan":
					int planned = workspace.Files.Count(f => workspace.GetDepth(f.Path) != "");
					int deep = workspace.Files.Count(f => workspace.GetDepth(f.Path) == "deep");
					phase.AutoConditionMet = total == 0 || planned == total;
					phase.Facts = total == 0 ? "" : $"{planned} of {total} file(s) given a depth ({deep} deep).";
					break;
				case "traverse":
					// Trust-marked files are deliberately out of scope for the deep read.
					var inScope = workspace.Files.Where(f => workspace.GetDepth(f.Path) != "trust").ToList();
					int scopeViewed = inScope.Count(f => workspace.Store.IsViewed(f.Path));
					phase.AutoConditionMet = total > 0 && scopeViewed == inScope.Count;
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
					phase.Facts = $"{scopeViewed} of {inScope.Count} in-scope file(s) viewed.{session}";
					break;
				case "sweep":
					phase.Facts = SweepItems.Count == 0
						? "No findings (or sweep not run - it runs when you enter this phase)."
						: $"{SweepItems.Count} computed finding(s) below.";
					break;
				case "close":
					int drafts = workspace.Drafts.Count;
					phase.Facts = (drafts == 0 ? "No pending drafts." : $"{drafts} draft(s) pending.")
						+ " 'Review record' writes the close-out artifact.";
					break;
			}
			phase.IsSatisfied = phase.AutoConditionMet && (!phase.RequiresCheck || phase.IsChecked);
		}
		int done = Phases.Count(p => p.IsSatisfied);
		State.Progress = $"{done} of {Phases.Count} phases complete{(State.OverrideGate ? "  (gate overridden)" : "")}";
	}

	(bool Ok, string Detail) Gate()
	{
		if (State.OverrideGate)
			return (true, "");
		var unmet = Phases.Where(p => !p.IsSatisfied).Select(p => p.Title).ToList();
		return unmet.Count == 0
			? (true, "")
			: (false, string.Join("; ", unmet));
	}

	internal static bool IsTestPath(string path)
		=> path.Contains("test", StringComparison.OrdinalIgnoreCase);
}
