using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.GitHub;
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
}

/// <summary>
/// Open pull requests of the repository, loaded through gh. Double-click opens a review.
/// </summary>
public class PrListPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;

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
				State.Status = ex.Message;
			}
		}
	}

	public async Task LoadAsync()
	{
		State.Status = "Loading pull requests...";
		State.Loading = true;
		Items.Clear();
		try
		{
			if (!await workspace.Git.IsRepositoryAsync())
			{
				State.Status = $"Not a git repository: {workspace.RepoPath}";
				return;
			}
			var prs = await workspace.GitHub.ListOpenPrsAsync();
			string viewer = "";
			try
			{
				viewer = await workspace.GitHub.GetViewerLoginAsync();
			}
			catch (ToolFailedException)
			{
				// Without a login nothing can be attributed to the reader; the list is still
				// worth showing, only without the "approved by you" badge.
			}
			// What still needs a reader comes first. A pull request someone has already
			// approved or asked for changes on has had its review, one the reader approved
			// themselves is done with them, and a draft is not asking for one yet - so all
			// three sink, drafts furthest. The sort is stable, so within each group the order
			// gh returned (most recently updated first) survives.
			foreach (var pr in prs.Select(p => p with { ViewerLogin = viewer })
				.OrderBy(p => p.IsDraft ? 3 : p.ApprovedByMe ? 2 : p.IsApproved || p.ChangesRequested ? 1 : 0))
				Items.Add(pr);
			State.Status = $"{prs.Count} open pull request(s)";
		}
		catch (ToolFailedException ex)
		{
			State.Status = ex.Message;
		}
		finally
		{
			State.Loading = false;
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
				State.Status = ex.Message;
			}
		}
	}
}
