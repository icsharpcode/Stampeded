using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

namespace Stampeded.Panes;

public sealed partial class FileBrowserState : ObservableObject
{
	[ObservableProperty]
	string status = "Open a review to browse its head worktree.";
}

public sealed class FsNode(string absPath, bool isDirectory)
{
	static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) {
		"bin", "obj", ".git", ".vs", "node_modules",
	};

	ObservableCollection<FsNode>? children;

	public string AbsPath { get; } = absPath;
	public bool IsDirectory { get; } = isDirectory;
	public string Title { get; } = Path.GetFileName(absPath);

	// Populated on first access so the tree lazily enumerates one level ahead of what
	// is visible instead of walking the whole worktree up front.
	public ObservableCollection<FsNode> Children => children ??= Load();

	ObservableCollection<FsNode> Load()
	{
		var result = new ObservableCollection<FsNode>();
		if (!IsDirectory)
			return result;
		try
		{
			foreach (var dir in Directory.EnumerateDirectories(AbsPath).Order(StringComparer.OrdinalIgnoreCase))
			{
				if (!SkippedDirectories.Contains(Path.GetFileName(dir)))
					result.Add(new FsNode(dir, isDirectory: true));
			}
			foreach (var file in Directory.EnumerateFiles(AbsPath).Order(StringComparer.OrdinalIgnoreCase))
				result.Add(new FsNode(file, isDirectory: false));
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}
		return result;
	}
}

/// <summary>
/// Full directory tree of the review's head worktree, so any file - changed or not - can
/// be opened as a navigable source document (semantic highlighting, go to definition,
/// find references all work there).
/// </summary>
public class FileBrowserPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	string? currentRoot;

	public ObservableCollection<FsNode> Roots { get; } = [];
	public FileBrowserState State { get; } = new();

	public FileBrowserPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.SemanticsChanged += () => Dispatcher.UIThread.Post(Rebuild);
	}

	void Rebuild()
	{
		string? root = workspace.WorktreePath;
		if (root == currentRoot)
			return;
		currentRoot = root;
		Roots.Clear();
		if (root is null || !Directory.Exists(root))
		{
			State.Status = "Open a review to browse its head worktree.";
			return;
		}
		foreach (var child in new FsNode(root, isDirectory: true).Children)
			Roots.Add(child);
		State.Status = $"Head worktree: {root}. Double-click a file to open it.";
	}

	public void Open(FsNode node)
	{
		if (node.IsDirectory || workspace.WorktreePath is not { } root)
			return;
		string rel = Path.GetRelativePath(root, node.AbsPath).Replace('\\', '/');
		workspace.NavigateToFileLineAsync(rel, 1, oldSide: false, record: true).HandleExceptions();
	}
}
