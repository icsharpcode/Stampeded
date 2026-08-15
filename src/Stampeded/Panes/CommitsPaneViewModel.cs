using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Avalonia.Media;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Panes;

public sealed partial class CommitsState : ObservableObject
{
	[ObservableProperty]
	string status = "Open a review to list its commits.";

	/// <summary>The selected commit's whole message. The list can only show the subject,
	/// and a commit whose reasoning lives in its body says nothing there.</summary>
	[ObservableProperty]
	string selectedMessage = "";
}

public sealed record CommitRow(CommitInfo Commit, bool IsUncommitted = false)
{
	public string Display
	{
		get
		{
			if (IsUncommitted)
				return $"uncommitted   {Commit.Subject}";
			string author = Commit.Author.Length > 14 ? Commit.Author[..14] : Commit.Author;
			return $"{Commit.ShortSha}  {Commit.Date}  {author,-14}  {Commit.Subject}";
		}
	}

	public FontWeight Weight => IsUncommitted ? FontWeight.SemiBold : FontWeight.Normal;
}

/// <summary>A file within a commit, or - when the sha is empty - within the working tree.</summary>
public sealed record CommitFileRow(string Sha, char Status, string Path)
{
	public string Display => $"{Status}  {Path}";

	public bool IsUncommitted => Sha.Length == 0;
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
		if (workspace.CommitRange is not { } range)
			return;
		(string baseSha, string headSha) = range;
		State.Status = "Loading commits...";
		try
		{
			// The working tree leads the list when the review's head is one: it sits on top
			// of every commit below it.
			if (workspace.DirtyWorktreePath is { } dirty)
			{
				var pending = await workspace.Git.DiffWorkingTreeAsync(dirty, "HEAD");
				if (pending.Count > 0)
				{
					Commits.Add(new CommitRow(
						new CommitInfo("", "uncommitted", "", "", $"{pending.Count} file(s) not committed"),
						IsUncommitted: true));
				}
			}
			var commits = await workspace.Git.LogAsync($"{baseSha}..{headSha}", null, follow: false, limit: 200);
			foreach (var commit in commits)
				Commits.Add(new CommitRow(commit));
			State.Status = $"{commits.Count} commit(s) in the review range"
				+ (Commits.Count > commits.Count ? ", plus uncommitted work" : "")
				+ ". Select one to see its files.";
		}
		catch (ToolFailedException ex)
		{
			State.Status = ex.Message;
		}
	}

	public void SelectCommit(CommitRow row)
	{
		State.SelectedMessage = row.IsUncommitted ? "" : row.Commit.Message;
		if (row.IsUncommitted)
			LoadUncommittedFilesAsync().HandleExceptions();
		else
			LoadFilesAsync(row.Commit).HandleExceptions();
	}

	async Task LoadUncommittedFilesAsync()
	{
		CommitFiles.Clear();
		if (workspace.DirtyWorktreePath is not { } dirty)
			return;
		var files = await workspace.Git.DiffWorkingTreeAsync(dirty, "HEAD");
		foreach (var file in files)
		{
			char status = file.Kind switch {
				Core.Diff.FileChangeKind.Added => 'A',
				Core.Diff.FileChangeKind.Deleted => 'D',
				Core.Diff.FileChangeKind.Renamed => 'R',
				_ => 'M',
			};
			CommitFiles.Add(new CommitFileRow("", status, file.Path));
		}
		State.Status = $"Uncommitted in {dirty}  -  {files.Count} file(s); double-click to open.";
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
		// Uncommitted files have no commit to diff against a parent; the review's own diff
		// of that file already shows the working tree.
		if (row.IsUncommitted)
			workspace.NavigateToFileLineAsync(row.Path, 1, oldSide: false, record: true).HandleExceptions();
		else
			workspace.OpenHistoricalDiffAsync(row.Sha, row.Path).HandleExceptions();
	}
}
