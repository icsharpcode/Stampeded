using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Git;
using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;
using Stampeded.Panes;

namespace Stampeded.Documents;

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

public sealed partial class StartState : ObservableObject
{
	[ObservableProperty]
	string prColumnHeader = "Pull Requests";

	[ObservableProperty]
	string branchColumnHeader = "Branches";

	/// <summary>True while a review is opening: the preparation checklist overlays the
	/// window and the overview opens once the load-bearing signals are in.</summary>
	[ObservableProperty]
	bool isPreparing;

	/// <summary>Current frame of the pending-item spinner.</summary>
	[ObservableProperty]
	string spinner = "⠋";
}

/// <summary>
/// The start page: recent repositories, open pull requests and local branches, docked as
/// a document. Opening a review shows the preparation overlay, then the overview
/// document and the diff tabs in likely review order.
/// </summary>
public class StartDocumentViewModel : Document
{
	static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

	readonly ReviewWorkspace workspace;
	readonly DispatcherTimer spinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
	int spinnerFrame;
	bool openOverviewWhenReady;

	public StartState State { get; } = new();
	public ObservableCollection<string> Recents { get; } = new(RecentRepos.Load());
	public ObservableCollection<BranchRow> Branches { get; } = [];
	public ObservableCollection<PrepareItem> PrepareItems { get; } = [];
	public PrListPaneViewModel PrList { get; }

	IReadOnlyList<BranchInfo> rawBranches = [];
	string defaultBase = "origin/master";

	public StartDocumentViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		Title = "Start";
		PrList = new PrListPaneViewModel(workspace);
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
		PrList.Items.CollectionChanged += (_, _) => {
			State.PrColumnHeader = $"Pull Requests ({PrList.Items.Count})";
			AnnotateBranches();
		};
		workspace.ReviewChanged += () => Dispatcher.UIThread.Post(UpdatePreparation);
		workspace.SemanticsChanged += () => Dispatcher.UIThread.Post(UpdatePreparation);
		workspace.ChangeMapChanged += () => Dispatcher.UIThread.Post(UpdatePreparation);
		workspace.ChecksLoaded += () => Dispatcher.UIThread.Post(UpdatePreparation);
		workspace.ChurnChanged += () => Dispatcher.UIThread.Post(UpdatePreparation);
		workspace.CommentsChanged += () => Dispatcher.UIThread.Post(UpdatePreparation);
		spinnerTimer.Tick += (_, _) => {
			spinnerFrame = (spinnerFrame + 1) % SpinnerFrames.Length;
			State.Spinner = SpinnerFrames[spinnerFrame];
		};
		State.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(StartState.IsPreparing))
			{
				if (State.IsPreparing)
					spinnerTimer.Start();
				else
					spinnerTimer.Stop();
			}
		};
		LoadStartPageAsync().HandleExceptions();
	}

	async Task LoadStartPageAsync()
	{
		try
		{
			defaultBase = await workspace.Git.GetDefaultBaseAsync();
			rawBranches = await workspace.Git.ListBranchesAsync();
		}
		catch (ToolFailedException)
		{
			// Not a repo or no origin; the start page simply shows no branches.
		}
		AnnotateBranches();
		State.BranchColumnHeader = $"Branches ({rawBranches.Count})";
	}

	void AnnotateBranches()
	{
		var prsByBranch = PrList.Items
			.GroupBy(p => p.HeadRefName, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
		Branches.Clear();
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
		foreach (var row in rows.OrderBy(r => r.HasPrTag ? 0 : 1))
			Branches.Add(row);
	}

	public void OpenPr(PrSummary pr) => OpenPrNumber(pr.Number);

	public void OpenPrNumber(int number)
	{
		BeginPreparation();
		workspace.OpenPrAsync(number).HandleExceptions();
	}

	public void OpenBranch(BranchInfo branch)
	{
		BeginPreparation();
		workspace.OpenLocalRangeAsync(defaultBase, branch.Name).HandleExceptions();
	}

	public void OpenRecent(string path) => App.OpenRepositoryAsync(path).HandleExceptions();

	void BeginPreparation()
	{
		openOverviewWhenReady = true;
		State.IsPreparing = true;
		UpdatePreparation();
	}

	public void ContinueNow()
	{
		openOverviewWhenReady = false;
		State.IsPreparing = false;
		if (workspace.HeadSha is not null)
			workspace.OpenOverview();
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

	void UpdatePreparation()
	{
		bool reviewOpen = workspace.HeadSha is not null;
		Set(0, reviewOpen ? ($"{workspace.Files.Count} changed file(s)", true) : ("waiting", false));
		Set(1, SemanticStatus(workspace.Semantics));
		Set(2, SemanticStatus(workspace.BaseSemantics));
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

		// Load-bearing: both semantic sides terminal, CI and churn in; the map and
		// comments trail behind harmlessly (and may legitimately stay empty).
		bool ready = reviewOpen && PrepareItems[1].Done && PrepareItems[2].Done
			&& PrepareItems[4].Done && PrepareItems[5].Done;
		if (ready && openOverviewWhenReady && State.IsPreparing)
		{
			openOverviewWhenReady = false;
			State.IsPreparing = false;
			workspace.OpenOverview();
		}

		void Set(int index, (string Status, bool Done) value)
		{
			PrepareItems[index].Status = value.Status;
			PrepareItems[index].Done = value.Done;
		}
	}
}
