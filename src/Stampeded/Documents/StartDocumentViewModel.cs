using System.Collections.ObjectModel;

using Avalonia.Media;
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

/// <summary>A local branch or a stash on the start page. Branches are annotated with
/// their associated PR when one exists - including whether the local head differs from
/// what the PR is showing.</summary>
public sealed record BranchRow(BranchInfo Info, string PrTag, int? PrNumber = null, bool IsStash = false,
	BranchSync? Sync = null)
{
	public bool HasPrTag => PrTag.Length > 0;

	public bool IsBranch => !IsStash;

	public bool HasSync => Sync is not null;

	public string SyncText => Sync?.Display ?? "";

	public string SyncTip => Sync?.Explanation ?? "";

	public IBrush SyncBrush => Sync?.State switch {
		BranchSyncState.InSync => SyncBrushes.InSync,
		BranchSyncState.Ahead or BranchSyncState.Behind => SyncBrushes.Partial,
		BranchSyncState.Diverged => SyncBrushes.Diverged,
		_ => SyncBrushes.Unknown,
	};
}

static class SyncBrushes
{
	public static readonly IBrush InSync = new SolidColorBrush(Color.Parse("#2EA043"));
	public static readonly IBrush Partial = new SolidColorBrush(Color.Parse("#D29922"));
	public static readonly IBrush Diverged = new SolidColorBrush(Color.Parse("#F85149"));
	public static readonly IBrush Unknown = new SolidColorBrush(Color.Parse("#8B949E"));
}

public sealed partial class StartState : ObservableObject
{
	[ObservableProperty]
	string prColumnHeader = "Pull Requests";

	[ObservableProperty]
	string branchesLabel = "Branches";

	[ObservableProperty]
	string stashesLabel = "Stashes";

	/// <summary>Whether the third column lists stashes instead of branches.</summary>
	[ObservableProperty]
	bool showStashes;

	[ObservableProperty]
	string status = "";

	/// <summary>True while a review is opening: the preparation checklist overlays the
	/// window and the overview opens once the load-bearing signals are in.</summary>
	[ObservableProperty]
	bool isPreparing;

}

/// <summary>
/// The start page: recent repositories, open pull requests and local branches, docked as
/// a document. Opening a review shows the preparation overlay, then the overview
/// document and the diff tabs in likely review order.
/// </summary>
public class StartDocumentViewModel : Document
{
	readonly ReviewWorkspace workspace;
	bool openOverviewWhenReady;

	public StartState State { get; } = new();
	public ObservableCollection<string> Recents { get; } = new(RecentRepos.Load());
	public ObservableCollection<BranchRow> Branches { get; } = [];
	public ObservableCollection<PrepareItem> PrepareItems { get; } = [];
	public PrListPaneViewModel PrList { get; }

	IReadOnlyList<BranchInfo> rawBranches = [];
	IReadOnlyList<BranchInfo> rawStashes = [];
	// Filled asynchronously for branches whose head is not the PR's; equality itself is
	// free from the two SHAs, only "by how much" costs a git call.
	readonly Dictionary<string, BranchSync> syncByBranch = [];
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
		workspace.StatusMessage += message => Dispatcher.UIThread.Post(() => State.Status = message);
		State.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(StartState.ShowStashes))
				AnnotateBranches();
		};
		LoadStartPageAsync().HandleExceptions();
	}

	async Task LoadStartPageAsync()
	{
		try
		{
			defaultBase = await workspace.Git.GetDefaultBaseAsync();
			rawBranches = await workspace.Git.ListBranchesAsync();
			rawStashes = await workspace.Git.ListStashesAsync();
		}
		catch (ToolFailedException)
		{
			// Not a repo or no origin; the start page simply shows no branches.
		}
		AnnotateBranches();
	}

	/// <summary>Re-reads branches and stashes after an operation changed them.</summary>
	async Task ReloadRefsAsync()
	{
		rawBranches = await workspace.Git.ListBranchesAsync();
		rawStashes = await workspace.Git.ListStashesAsync();
		AnnotateBranches();
	}

	void AnnotateBranches()
	{
		// Both counts stay visible whichever list is showing: the pair of options is the
		// column header, so it also reports what the other option holds.
		State.BranchesLabel = $"Branches ({rawBranches.Count})";
		State.StashesLabel = $"Stashes ({rawStashes.Count})";
		if (State.ShowStashes)
		{
			Branches.Clear();
			foreach (var stash in rawStashes)
				Branches.Add(new BranchRow(stash, "", null, IsStash: true));
			return;
		}
		var prsByBranch = PrList.Items
			.GroupBy(p => p.HeadRefName, StringComparer.Ordinal)
			.ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
		Branches.Clear();
		var rows = new List<BranchRow>();
		foreach (var branch in rawBranches)
		{
			string tag = "";
			int? prNumber = null;
			BranchSync? sync = null;
			if (prsByBranch.TryGetValue(branch.Name, out var pr))
			{
				tag = $"PR #{pr.Number}";
				prNumber = pr.Number;
				sync = pr.HeadRefOid is { Length: > 0 } oid
					? string.Equals(oid, branch.Sha, StringComparison.OrdinalIgnoreCase)
						? BranchSync.InSync
						: syncByBranch.GetValueOrDefault(branch.Name, BranchSync.Unfetched)
					: null;
			}
			rows.Add(new BranchRow(branch, tag, prNumber, IsStash: false, sync));
		}
		foreach (var row in rows.OrderBy(r => r.HasPrTag ? 0 : 1))
			Branches.Add(row);
		RefreshSyncStatesAsync(prsByBranch).HandleExceptions();
	}

	/// <summary>Measures how far the branches that do not match their PR head have drifted.
	/// Only those need a git call, and only once each: a branch whose head equals the PR's
	/// is already known to be in sync.</summary>
	async Task RefreshSyncStatesAsync(Dictionary<string, PrSummary> prsByBranch)
	{
		bool changed = false;
		foreach (var branch in rawBranches)
		{
			if (!prsByBranch.TryGetValue(branch.Name, out var pr)
				|| pr.HeadRefOid is not { Length: > 0 } oid
				|| string.Equals(oid, branch.Sha, StringComparison.OrdinalIgnoreCase)
				|| syncByBranch.ContainsKey(branch.Name))
			{
				continue;
			}
			if (await workspace.Git.GetSyncStateAsync(branch.Sha, oid) is { } sync)
			{
				syncByBranch[branch.Name] = sync;
				changed = true;
			}
		}
		if (changed)
			AnnotateBranches();
	}

	public void OpenPr(PrSummary pr) => OpenPrNumber(pr.Number);

	public void OpenPrNumber(int number)
	{
		BeginPreparation();
		workspace.OpenPrAsync(number).HandleExceptions();
	}

	public void OpenBranch(BranchRow row)
	{
		BeginPreparation();
		// A stash reviews as the range from the commit it was taken on to the stash
		// commit itself, which is exactly the stashed change.
		var (baseRef, head) = row.IsStash
			? ($"{row.Info.Sha}^", row.Info.Sha)
			: (defaultBase, row.Info.Name);
		workspace.OpenLocalRangeAsync(baseRef, head).HandleExceptions();
	}

	/// <summary>Gives a stash a durable name by pointing a new branch at its commit. The
	/// stash stays in the stash list and no working tree is touched, so this is safe to
	/// do while the stash is still wanted where it is.</summary>
	public void CreateBranchFromStash(BranchRow row, string name)
	{
		if (!row.IsStash || string.IsNullOrWhiteSpace(name))
			return;
		CreateAsync().HandleExceptions();

		async Task CreateAsync()
		{
			try
			{
				await workspace.Git.CreateBranchAsync(name.Trim(), row.Info.Sha);
				await ReloadRefsAsync();
				State.Status = $"Created branch '{name.Trim()}' at {row.Info.Name} (the stash is unchanged).";
			}
			catch (ToolFailedException ex)
			{
				State.Status = $"Could not create the branch: {ex.Message}";
			}
		}
	}

	/// <summary>Rebases a local branch onto the default base in a throwaway worktree. On
	/// conflict the branch is left untouched; on success the pre-rebase SHA is reported so
	/// the old state can be recovered.</summary>
	public void RebaseBranch(BranchRow row)
	{
		if (row.IsStash)
			return;
		RebaseAsync().HandleExceptions();

		async Task RebaseAsync()
		{
			State.Status = $"Rebasing {row.Info.Name} onto {defaultBase}...";
			try
			{
				string before = await workspace.Git.RebaseBranchAsync(row.Info.Name, defaultBase);
				await ReloadRefsAsync();
				State.Status = $"Rebased {row.Info.Name} onto {defaultBase}. Previous head was {before[..9]} "
					+ $"(recover with: git branch -f {row.Info.Name} {before[..9]}).";
			}
			catch (ToolFailedException ex)
			{
				State.Status = $"Rebase of {row.Info.Name} failed, branch left unchanged: {ex.Message}";
			}
		}
	}

	/// <summary>Rebases a PR branch onto its target, server-side via the GitHub API.</summary>
	public void RebasePr(BranchRow row)
	{
		if (row.PrNumber is { } number)
			workspace.RebasePrAsync(number).HandleExceptions();
	}

	public void OpenRecent(string path) => App.OpenRepositoryAsync(path).HandleExceptions();

	public void OpenPrOnGitHub(PrSummary pr)
		=> workspace.OpenOnGitHubAsync(pr.Number).HandleExceptions();

	public void OpenBranchPrOnGitHub(BranchRow row)
	{
		if (row.PrNumber is { } number)
			workspace.OpenOnGitHubAsync(number).HandleExceptions();
	}

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
