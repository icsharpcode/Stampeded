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

/// <summary>How a branch stands against the default branch.</summary>
public enum MergeState
{
	/// <summary>Not answered yet, or genuinely not in the default branch.</summary>
	Unknown,
	/// <summary>Its tip is an ancestor of the default branch.</summary>
	Merged,
	/// <summary>Its commits are not in the default branch, but every one of them has an
	/// equivalent patch there: what a rebase merge leaves behind.</summary>
	RebaseMerged,
}

/// <summary>A local branch or a stash on the start page. Branches are annotated with
/// their associated PR when one exists - including whether the local head differs from
/// what the PR is showing.</summary>
public sealed record BranchRow(BranchInfo Info, string PrTag, int? PrNumber = null, bool IsStash = false,
	BranchSync? Sync = null, MergeState Merge = MergeState.Unknown, bool IsDefault = false,
	string? WorktreePath = null)
{
	public bool HasPrTag => PrTag.Length > 0;

	public bool IsBranch => !IsStash;

	/// <summary>Whether some checkout has this branch, which is where its files are.</summary>
	public bool HasWorktree => !IsStash && WorktreePath is not null;

	/// <summary>The default branch carries its own label rather than being reported as
	/// merged into itself.</summary>
	public bool ShowMerge => !IsStash && !IsDefault && Merge != MergeState.Unknown;

	public bool ShowDefault => !IsStash && IsDefault;

	public string MergeText => Merge == MergeState.RebaseMerged ? "rebase-merged" : "merged";

	public string MergeTip => Merge == MergeState.RebaseMerged
		? "Every commit on this branch already exists in the default branch as an equivalent "
			+ "patch, which is what a rebase merge leaves behind. The branch itself can go."
		: "This branch's tip is an ancestor of the default branch, so it is in as it stands.";

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

	/// <summary>True while the refs are being read - and, the first time, while gh is asked
	/// which branch is the default, which is the part that hangs without a network.</summary>
	[ObservableProperty]
	bool refsLoading;

	/// <summary>Why the ref list is empty, when it is.</summary>
	[ObservableProperty]
	string refsStatus = "";

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
	// Keyed by branch tip, so the answer survives a reload but is recomputed the moment the
	// branch moves. Ancestry is answered for every branch in one call; the patch-equivalence
	// that catches a rebase merge costs a call each and is filled in behind the list. The
	// answers hold only for the default branch they were measured against, which is what
	// mergeCheckedAgainst records.
	readonly Dictionary<string, MergeState> mergeByBranchSha = [];
	string? mergeCheckedAgainst;
	IReadOnlySet<string> mergedBranches = new HashSet<string>();
	// Which checkout has each branch, so a row can offer to open it - and so the reader can
	// see at a glance which branches are laid out on disk somewhere.
	IReadOnlyDictionary<string, string> worktreeByBranch = new Dictionary<string, string>();
	string defaultBase = "origin/master";
	string defaultBranch = "master";

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
			"Generated sources (builds both sides)",
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
		workspace.GeneratedSourcesChanged += () => Dispatcher.UIThread.Post(UpdatePreparation);
		workspace.StatusMessage += message => Dispatcher.UIThread.Post(() => State.Status = message);
		State.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(StartState.ShowStashes))
				AnnotateBranches();
		};
		LoadStartPageAsync().HandleExceptions();
	}

	async Task LoadStartPageAsync()
	{
		State.RefsLoading = true;
		try
		{
			defaultBranch = await workspace.GetDefaultBranchAsync();
			defaultBase = await workspace.GetDefaultBaseAsync();
			rawBranches = await workspace.Git.ListBranchesAsync();
			rawStashes = await workspace.Git.ListStashesAsync();
			mergedBranches = await workspace.Git.ListMergedBranchesAsync(defaultBase);
			worktreeByBranch = (await workspace.Git.ListWorktreesAsync())
				.Where(w => w.Branch is not null)
				.ToDictionary(w => w.Branch!, w => w.Path, StringComparer.Ordinal);
		}
		catch (ToolFailedException ex)
		{
			// Not a repo, or no origin to ask about the default branch: the list is empty, and
			// saying why beats an empty box.
			State.RefsStatus = ex.Message;
		}
		finally
		{
			State.RefsLoading = false;
		}
		AnnotateBranches();
	}

	public void ReloadRefs() => ReloadRefsAsync().HandleExceptions();

	/// <summary>Re-reads branches and stashes after an operation changed them.</summary>
	async Task ReloadRefsAsync()
	{
		State.RefsLoading = true;
		try
		{
			await ReloadRefsCoreAsync();
		}
		finally
		{
			State.RefsLoading = false;
		}
	}

	async Task ReloadRefsCoreAsync()
	{
		rawBranches = await workspace.Git.ListBranchesAsync();
		rawStashes = await workspace.Git.ListStashesAsync();
		try
		{
			mergedBranches = await workspace.Git.ListMergedBranchesAsync(defaultBase);
			worktreeByBranch = (await workspace.Git.ListWorktreesAsync())
				.Where(w => w.Branch is not null)
				.ToDictionary(w => w.Branch!, w => w.Path, StringComparer.Ordinal);
		}
		catch (ToolFailedException)
		{
			// No origin, or the default base is not fetched: the labels stay off rather than
			// the whole list failing to reload.
		}
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
			var merge = mergedBranches.Contains(branch.Name)
				? MergeState.Merged
				: mergeByBranchSha.GetValueOrDefault(branch.Sha, MergeState.Unknown);
			rows.Add(new BranchRow(branch, tag, prNumber, IsStash: false, sync, merge,
				IsDefault: string.Equals(branch.Name, defaultBranch, StringComparison.Ordinal),
				WorktreePath: worktreeByBranch.GetValueOrDefault(branch.Name)));
		}
		foreach (var row in rows.OrderBy(r => r.HasPrTag ? 0 : 1))
			Branches.Add(row);
		RefreshSyncStatesAsync(prsByBranch).HandleExceptions();
		RefreshRebaseMergedAsync().HandleExceptions();
	}

	/// <summary>
	/// Asks the more expensive question only of the branches the cheap one did not answer:
	/// whether a branch that is not an ancestor of the default branch nonetheless has all of
	/// its commits there as equivalent patches, which is what a rebase merge produces. One
	/// git call per branch, so it runs behind the list rather than delaying it, and the
	/// answer is cached against the branch tip.
	/// </summary>
	async Task RefreshRebaseMergedAsync()
	{
		// The cached answers were measured against the default branch as it stood. When a
		// fetch moves it - which is the moment a branch becomes rebase-merged - every one of
		// them is about a history that no longer exists, so they all go.
		string? baseSha = await workspace.Git.TryRevParseAsync(defaultBase);
		if (baseSha is null)
			return;
		if (!string.Equals(baseSha, mergeCheckedAgainst, StringComparison.Ordinal))
		{
			mergeByBranchSha.Clear();
			mergeCheckedAgainst = baseSha;
		}
		bool changed = false;
		foreach (var branch in rawBranches.ToList())
		{
			if (mergedBranches.Contains(branch.Name)
				|| string.Equals(branch.Name, defaultBranch, StringComparison.Ordinal)
				|| mergeByBranchSha.ContainsKey(branch.Sha))
			{
				continue;
			}
			try
			{
				mergeByBranchSha[branch.Sha] = await workspace.Git.IsMergedByPatchAsync(branch.Name, defaultBase)
					? MergeState.RebaseMerged
					: MergeState.Unknown;
				changed = true;
			}
			catch (ToolFailedException)
			{
				// One unreadable branch does not stop the rest from being labelled.
			}
		}
		if (changed)
			AnnotateBranches();
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

	/// <summary>Rebases a local branch onto the default base, in a throwaway worktree unless
	/// a checkout already has the branch. On conflict the branch is left untouched; on success
	/// the pre-rebase SHA is reported so the old state can be recovered.</summary>
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
				var result = await workspace.Git.RebaseBranchAsync(row.Info.Name, defaultBase);
				await ReloadRefsAsync();
				State.Status = result.Outcome == RebaseOutcome.Conflicted
					? $"Rebase of {row.Info.Name} stopped on conflicts the merge tool did not resolve. "
						+ $"It is still in progress in {result.WorkingDirectory} - finish it with "
						+ "`git rebase --continue` there, or undo it with `git rebase --abort`."
					: $"Rebased {row.Info.Name} onto {defaultBase}. Previous head was {result.Before[..9]} "
						+ $"(recover with: {result.RecoveryCommand(row.Info.Name)}).";
			}
			catch (ToolFailedException ex)
			{
				State.Status = $"Rebase of {row.Info.Name} failed, branch left unchanged: {ex.Message}";
			}
		}
	}

	/// <summary>Updates every remote-tracking ref, then re-reads the lists. Sync states are
	/// measured against commits that have to be present locally, so the ones showing as
	/// "differs" resolve into real ahead/behind counts once this has run.</summary>
	public void Fetch()
	{
		FetchAsync().HandleExceptions();

		async Task FetchAsync()
		{
			State.Status = "Fetching from origin...";
			try
			{
				await workspace.Git.FetchAsync();
				syncByBranch.Clear();
				await ReloadRefsAsync();
				await PrList.LoadAsync();
				State.Status = "Fetched from origin.";
			}
			catch (ToolFailedException ex)
			{
				State.Status = $"Fetch failed: {ex.Message}";
			}
		}
	}

	/// <summary>Brings origin's copy of a branch in: creates it locally when it is not there
	/// yet, fast-forwards it when it is behind. Diverged branches are left alone - that is
	/// what the rebase command is for.</summary>
	public void PullBranch(string branch)
	{
		PullAsync().HandleExceptions();

		async Task PullAsync()
		{
			State.Status = $"Pulling {branch} from origin...";
			try
			{
				var result = await workspace.Git.PullBranchAsync(branch);
				syncByBranch.Remove(branch);
				await ReloadRefsAsync();
				State.Status = result.Outcome switch {
					PullOutcome.Created => $"Created {branch} at origin's {result.Sha[..9]}.",
					PullOutcome.FastForwarded => $"Fast-forwarded {branch} to {result.Sha[..9]}.",
					PullOutcome.AlreadyUpToDate => $"{branch} is already up to date.",
					_ => $"{branch} has diverged from origin's copy - nothing changed. "
						+ "Rebase it instead, or reset it if the local commits are expendable.",
				};
			}
			catch (ToolFailedException ex)
			{
				State.Status = $"Pull of {branch} failed, branch left unchanged: {ex.Message}";
			}
		}
	}

	public void PullBranchRow(BranchRow row)
	{
		if (!row.IsStash)
			PullBranch(row.Info.Name);
	}

	/// <summary>Pushes a branch to origin, replacing origin's copy with --force-with-lease
	/// when the two have diverged, which is what a rebase leaves behind.</summary>
	public void PushBranch(string branch)
	{
		PushAsync().HandleExceptions();

		async Task PushAsync()
		{
			State.Status = $"Pushing {branch} to origin...";
			try
			{
				var result = await workspace.Git.PushBranchAsync(branch);
				syncByBranch.Remove(branch);
				await ReloadRefsAsync();
				State.Status = result.Outcome switch {
					PushOutcome.Created => $"Pushed {branch} to origin, which did not have it before.",
					PushOutcome.Pushed => $"Pushed {branch} to origin ({result.Sha[..9]}).",
					PushOutcome.ForcePushed => $"Force-pushed {branch} to origin ({result.Sha[..9]}); "
						+ "the branches had diverged, so origin's copy was replaced.",
					_ => $"origin is already at {branch} ({result.Sha[..9]}).",
				};
			}
			catch (ToolFailedException ex)
			{
				State.Status = $"Push of {branch} failed, origin unchanged: {ex.Message}";
			}
		}
	}

	public void PushBranchRow(BranchRow row)
	{
		if (!row.IsStash)
			PushBranch(row.Info.Name);
	}

	/// <summary>Opens the checkout that has this branch in the desktop's file manager.</summary>
	public void OpenWorktree(BranchRow row)
	{
		if (row.WorktreePath is { } path)
			workspace.OpenUrlAsync(path).HandleExceptions();
	}

	/// <summary>Deletes a branch that is already in the default branch. Offered only for
	/// those, so there is nothing to lose; the commit it pointed at is reported anyway,
	/// since that is all it takes to put the branch back.</summary>
	public void DeleteBranchRow(BranchRow row)
	{
		if (!row.ShowMerge)
			return;
		DeleteAsync().HandleExceptions();

		async Task DeleteAsync()
		{
			string branch = row.Info.Name;
			BranchDeletion deletion;
			try
			{
				deletion = await workspace.Git.DeleteBranchAsync(branch);
			}
			catch (Exception ex) when (ex is ToolFailedException or RefusedException)
			{
				// Only the deletion itself is caught here. Reporting a failure for anything
				// that goes wrong afterwards would claim the branch is still there when it
				// is not - and a worktree with uncommitted work lands in this branch too,
				// which is the case where being told nothing happened matters most.
				State.Status = $"Delete of {branch} failed, nothing was removed: {ex.Message}";
				return;
			}
			mergeByBranchSha.Remove(deletion.Sha);
			await ReloadRefsAsync();
			State.Status = $"Deleted {branch}, which was {row.MergeText}"
				+ (deletion.RemovedWorktree is { } path ? $", and its worktree {path}" : "")
				+ $" (restore with: git branch {branch} {deletion.Sha[..9]}).";
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
		Set(7, (workspace.GeneratedSourcesStatus, workspace.GeneratedSourcesDone));

		// The diff is the only thing a review cannot start without: it is what the reader
		// reads. Semantics, the change map, CI, churn and comments all arrive into a window
		// that is already being used, and the commands that need them stay disabled until
		// they do - which beats a minute of watching a checklist fill in.
		bool ready = reviewOpen;
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
