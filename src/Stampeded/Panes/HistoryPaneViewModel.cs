using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;
using Stampeded.Documents;

namespace Stampeded.Panes;

public sealed partial class HistoryState : ObservableObject
{
	[ObservableProperty]
	string status = "Focus a diff to see its file's history; select text and use 'History of Selection' for a pickaxe search.";
}

/// <summary>
/// Per-file history: follows the active diff document with `git log --follow` (full
/// history, not just the review range), or shows a pickaxe search over selected text.
/// Double-click opens that commit's change to the file as a historical diff.
/// </summary>
public class HistoryPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	string? currentPath;

	public ObservableCollection<CommitRow> Commits { get; } = [];
	public HistoryState State { get; } = new();

	public HistoryPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		DiffDocumentView.ActiveViewChanged += () => Dispatcher.UIThread.Post(FollowActiveView);
		workspace.PickaxeRequested += (text, path) => PickaxeAsync(text, path).HandleExceptions();
	}

	void FollowActiveView()
	{
		var vm = DiffDocumentView.ActiveView?.ViewModel;
		if (vm is null || vm.Historical)
			return;
		string path = vm.File.Path;
		if (path == currentPath)
			return;
		currentPath = path;
		LoadAsync(path).HandleExceptions();
	}

	async Task LoadAsync(string path)
	{
		Commits.Clear();
		State.Status = $"History of {path}...";
		try
		{
			var commits = await workspace.Git.LogAsync(null, path, follow: true, limit: 50);
			foreach (var commit in commits)
				Commits.Add(new CommitRow(commit));
			State.Status = $"{commits.Count} commit(s) touching {path} (newest first).";
		}
		catch (ToolFailedException ex)
		{
			State.Status = ex.Message;
		}
	}

	async Task PickaxeAsync(string text, string path)
	{
		Commits.Clear();
		currentPath = path;
		string display = text.Length > 40 ? text[..40] + "..." : text;
		State.Status = $"Commits adding/removing '{display}' in {path}...";
		try
		{
			var commits = await workspace.Git.LogPickaxeAsync(text, path, limit: 50);
			Commits.Clear();
			foreach (var commit in commits)
				Commits.Add(new CommitRow(commit));
			State.Status = $"{commits.Count} commit(s) added or removed '{display}' in {path}.";
		}
		catch (ToolFailedException ex)
		{
			State.Status = ex.Message;
		}
	}

	public void Open(CommitRow row)
	{
		if (currentPath is not null)
			workspace.OpenHistoricalDiffAsync(row.Commit.Sha, currentPath).HandleExceptions();
	}
}
