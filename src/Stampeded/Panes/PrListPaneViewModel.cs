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
		LoadAsync().HandleExceptions();
	}

	public async Task LoadAsync()
	{
		State.Status = "Loading pull requests...";
		Items.Clear();
		try
		{
			if (!await workspace.Git.IsRepositoryAsync())
			{
				State.Status = $"Not a git repository: {workspace.RepoPath}";
				return;
			}
			var prs = await workspace.GitHub.ListOpenPrsAsync();
			foreach (var pr in prs)
				Items.Add(pr);
			State.Status = $"{prs.Count} open pull request(s)";
		}
		catch (ToolFailedException ex)
		{
			State.Status = ex.Message;
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
