using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Panes;

public sealed partial class CommitsState : ObservableObject
{
	[ObservableProperty]
	string status = "Open a review to list its commits.";
}

public sealed record CommitRow(CommitInfo Commit)
{
	public string Display
	{
		get
		{
			string author = Commit.Author.Length > 14 ? Commit.Author[..14] : Commit.Author;
			return $"{Commit.ShortSha}  {Commit.Date}  {author,-14}  {Commit.Subject}";
		}
	}
}

public sealed record CommitFileRow(string Sha, char Status, string Path)
{
	public string Display => $"{Status}  {Path}";
}

/// <summary>
/// The review range's commits, newest first; selecting one lists its files, and opening
/// a file shows that commit's change as a historical diff. Reading the change as its
/// commit narrative is an understanding overlay - the review scope stays the full range.
/// </summary>
public class CommitsPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;

	public ObservableCollection<CommitRow> Commits { get; } = [];
	public ObservableCollection<CommitFileRow> CommitFiles { get; } = [];
	public CommitsState State { get; } = new();

	public CommitsPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ReviewChanged += () => LoadAsync().HandleExceptions();
	}

	async Task LoadAsync()
	{
		Commits.Clear();
		CommitFiles.Clear();
		if (workspace.BaseSha is not { } baseSha || workspace.HeadSha is not { } headSha)
			return;
		State.Status = "Loading commits...";
		try
		{
			var commits = await workspace.Git.LogAsync($"{baseSha}..{headSha}", null, follow: false, limit: 200);
			foreach (var commit in commits)
				Commits.Add(new CommitRow(commit));
			State.Status = $"{commits.Count} commit(s) in the review range. Select one to see its files.";
		}
		catch (ToolFailedException ex)
		{
			State.Status = ex.Message;
		}
	}

	public void SelectCommit(CommitRow row)
	{
		LoadFilesAsync(row.Commit).HandleExceptions();
	}

	async Task LoadFilesAsync(CommitInfo commit)
	{
		CommitFiles.Clear();
		try
		{
			var files = await workspace.Git.DiffNameStatusAsync($"{commit.Sha}^", commit.Sha);
			foreach (var (status, path) in files)
				CommitFiles.Add(new CommitFileRow(commit.Sha, status, path));
			State.Status = $"{commit.ShortSha} {commit.Subject}  -  {files.Count} file(s); double-click to open.";
		}
		catch (ToolFailedException)
		{
			// Root commits have no parent; show the whole commit instead.
			State.Status = $"{commit.ShortSha} has no parent; open it as text via double-click.";
		}
	}

	public void OpenFile(CommitFileRow row)
	{
		workspace.OpenHistoricalDiffAsync(row.Sha, row.Path).HandleExceptions();
	}
}
