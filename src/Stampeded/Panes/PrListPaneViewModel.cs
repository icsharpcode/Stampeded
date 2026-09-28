using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.PullRequests;
using Stampeded.Core.Infra;

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
	/// <summary>Whose pull request it is - "GitHub", "Azure DevOps" - for the headers and
	/// tooltips that name the host.</summary>
	public string HostName => workspace.HostName;

	public ObservableCollection<PrSummary> Items { get; } = [];
	public PrListState State { get; } = new();

	public PrListPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.StatusMessage += message => State.Status = message;
		LoadAsync().HandleExceptions();
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

	public async Task LoadAsync()
	{
		int version = ++loadVersion;
		statsCts?.Cancel();
		statsCts = new CancellationTokenSource();
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
			string? originOwner = await workspace.Git.GetOriginOwnerAsync();
			string viewer = "";
			try
			{
				viewer = await workspace.Host.GetViewerLoginAsync();
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
			Items.Replace(rows);
			State.Loading = false;
		}
		if (loaded && workspace.Host is IPullRequestStatsProvider stats)
			LoadDeferredStatsAsync(stats, version, statsCts.Token).HandleExceptions();
	}

	async Task LoadDeferredStatsAsync(IPullRequestStatsProvider stats, int version, CancellationToken ct)
	{
		var rows = Items.ToList();
		foreach (var pr in rows)
			pr.BeginStatsLoading();
		State.StatsLoading = rows.Count > 0;
		var loaded = new Dictionary<int, PrDiffStats>();
		try
		{
			foreach (var pr in rows)
			{
				ct.ThrowIfCancellationRequested();
				if (version != loadVersion)
					return;
				try
				{
					var diff = await stats.GetDiffStatsAsync(pr.Number, ct);
					loaded[pr.Number] = diff;
				}
				catch (Exception ex) when (ex is ToolFailedException or System.Text.Json.JsonException)
				{
					CliLog.Write("host", $"pull request #{pr.Number} line counts unavailable: {ex.Message}");
				}
			}
			if (version != loadVersion)
				return;
			foreach (var pr in rows)
			{
				if (loaded.TryGetValue(pr.Number, out var diff))
					pr.SetDiffStats(diff);
				else
					pr.EndStatsLoading();
			}
		}
		finally
		{
			if (version == loadVersion)
				State.StatsLoading = false;
		}
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
