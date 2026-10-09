using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;
using Stampeded.Core.PullRequests;

namespace Stampeded.Panes;

public sealed partial class PrListState : ObservableObject
{
	[ObservableProperty]
	string status = "";

	/// <summary>True while gh is being asked. Reading the pull requests takes a second or two
	/// on a good connection and forever on none, and an empty list says neither.</summary>
	[ObservableProperty]
	bool loading;

	[ObservableProperty]
	bool statsLoading;
}

/// <summary>
/// Open pull requests of the repository, loaded through gh. Double-click opens a review.
/// </summary>
public class PrListPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	CancellationTokenSource? statsCts;
	int loadVersion;
	readonly HashSet<int> visibleStatsPriority = [];
	readonly SemaphoreSlim statsPriorityChanged = new(0);
	/// <summary>Whose pull request it is - "GitHub", "Azure DevOps" - for the headers and
	/// tooltips that name the host.</summary>
	public string HostName => workspace.HostName;

	public ObservableCollection<PrSummary> Items { get; } = [];
	public PrListState State { get; } = new();

	public PrListPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.StatusMessage += message => State.Status = message;
	}

	public void CancelBackgroundWork()
	{
		loadVersion++;
		statsCts?.Cancel();
		State.Loading = false;
		State.StatsLoading = false;
	}

	/// <summary>Opens a local base..head range review ("master..my-branch"; merge-base
	/// semantics like the PR flow).</summary>
	public void OpenRange(string rangeText)
	{
		var parts = rangeText.Split("..", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (parts.Length != 2)
		{
			State.Status = "Range must be <base>..<head>, e.g. origin/master..my-branch";
			return;
		}
		State.Status = $"Opening {parts[0]}..{parts[1]}...";
		OpenRangeCoreAsync(parts[0], parts[1]).HandleExceptions();

		async Task OpenRangeCoreAsync(string baseRef, string headRef)
		{
			try
			{
				await workspace.OpenLocalRangeAsync(baseRef, headRef);
				State.Status = $"Reviewing {baseRef}..{headRef}";
			}
			catch (ToolFailedException ex)
			{
				State.Status = ExternalTool.Explain(ex);
			}
		}
	}

	public async Task LoadAsync(CancellationToken ct = default)
	{
		int version = ++loadVersion;
		statsCts?.Cancel();
		statsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		State.Status = "Loading pull requests...";
		State.Loading = true;
		State.StatsLoading = false;
		var rows = new List<PrSummary>();
		bool loaded = false;
		try
		{
			if (!await workspace.Git.IsRepositoryAsync())
			{
				State.Status = $"Not a git repository: {workspace.RepoPath}";
				return;
			}
			var prs = await workspace.Host.ListOpenPrsAsync();
			ct.ThrowIfCancellationRequested();
			string? originOwner = await workspace.Git.GetOriginOwnerAsync(ct);
			string viewer = "";
			try
			{
				viewer = await workspace.Host.GetViewerLoginAsync(ct);
			}
			catch (ToolFailedException)
			{
				// Without a login nothing can be attributed to the reader; the list is still
				// worth showing, only without the "approved by you" badge.
			}
			// The sort is stable, so within each priority group the host's order survives.
			foreach (var pr in prs.Select(p => p with { ViewerLogin = viewer, OriginOwner = originOwner })
				.OrderBy(p => p.ListPriority))
				rows.Add(pr);
			State.Status = $"{prs.Count} open pull request(s)";
			loaded = true;
		}
		catch (ToolFailedException ex)
		{
			State.Status = ExternalTool.Explain(ex);
		}
		finally
		{
			if (!ct.IsCancellationRequested && version == loadVersion)
			{
				Items.Replace(rows);
				State.Loading = false;
			}
		}
		if (loaded)
			LoadDeferredStatsAsync(version, statsCts.Token).HandleExceptions();
	}

	async Task LoadDeferredStatsAsync(int version, CancellationToken ct)
	{
		var rows = Items.ToList();
		foreach (var pr in rows)
			pr.BeginStatsLoading();
		State.StatsLoading = rows.Count > 0;
		var pending = rows.ToDictionary(pr => pr.Number);
		var fetchedBaseBranches = new HashSet<string>(StringComparer.Ordinal);
		try
		{
			while (pending.Count > 0)
			{
				ct.ThrowIfCancellationRequested();
				if (version != loadVersion)
					return;
				PrSummary? pr = NextStatsRow(rows, pending, allowDeferred: false);
				if (pr is null)
				{
					await statsPriorityChanged.WaitAsync(TimeSpan.FromMilliseconds(900), ct);
					pr = NextStatsRow(rows, pending, allowDeferred: true);
					if (pr is null)
						continue;
				}
				try
				{
					var diff = await LoadDiffStatsAsync(pr, fetchedBaseBranches, ct);
					pr.SetDiffStats(diff);
				}
				catch (Exception ex) when (ex is ToolFailedException or System.Text.Json.JsonException)
				{
					CliLog.Write("host", $"pull request #{pr.Number} line counts unavailable: {ex.Message}");
					pr.EndStatsLoading();
				}
				pending.Remove(pr.Number);
			}
		}
		finally
		{
			if (version == loadVersion)
				State.StatsLoading = false;
		}
	}

	async Task<PrDiffStats> LoadDiffStatsAsync(PrSummary pr, HashSet<string> fetchedBaseBranches, CancellationToken ct)
	{
		if (fetchedBaseBranches.Add(pr.BaseRefName))
			await workspace.Git.FetchBranchAsync(pr.BaseRefName, ct);
		string target = await workspace.Git.RevParseAsync(await workspace.Git.RemoteBranchAsync(pr.BaseRefName, ct), ct);

		string head = pr.HeadRefOid is { Length: > 0 } oid && await workspace.Git.HasCommitAsync(oid, ct)
			? oid
			: await workspace.Git.FetchPrHeadAsync(await workspace.Host.PrHeadRefspecAsync(pr.Number, ct), pr.Number, ct);

		string mergeBase = await workspace.Git.GetMergeBaseAsync(target, head, ct);
		var stats = await workspace.Git.GetDiffStatsAsync(mergeBase, head, ct);
		return new PrDiffStats(stats.Added, stats.Removed, stats.ChangedFiles);
	}

	PrSummary? NextStatsRow(IReadOnlyList<PrSummary> rows, Dictionary<int, PrSummary> pending, bool allowDeferred)
	{
		foreach (var pr in rows)
			if (pending.ContainsKey(pr.Number) && visibleStatsPriority.Contains(pr.Number))
				return pr;
		return allowDeferred ? rows.FirstOrDefault(pr => pending.ContainsKey(pr.Number)) : null;
	}

	public void PrioritizeStatsFor(IEnumerable<PrSummary> visibleRows)
	{
		visibleStatsPriority.Clear();
		foreach (var pr in visibleRows)
			visibleStatsPriority.Add(pr.Number);
		if (statsPriorityChanged.CurrentCount == 0)
			statsPriorityChanged.Release();
	}

	public void Open(PrSummary pr)
	{
		State.Status = $"Opening #{pr.Number}...";
		OpenCoreAsync(pr).HandleExceptions();

		async Task OpenCoreAsync(PrSummary target)
		{
			try
			{
				await workspace.OpenPrAsync(target.Number);
				State.Status = $"Reviewing #{target.Number}: {target.Title}";
			}
			catch (ToolFailedException ex)
			{
				State.Status = ExternalTool.Explain(ex);
			}
		}
	}
}
