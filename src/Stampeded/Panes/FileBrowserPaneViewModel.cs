using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.TreeView;

namespace Stampeded.Panes;

public sealed partial class FileBrowserState : ObservableObject
{
	[ObservableProperty]
	string status = "Open a review to browse its head worktree.";
}

public sealed class FsNode(string absPath, bool isDirectory) : SharpTreeNode
{
	static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) {
		"bin", "obj", ".git", ".vs", "node_modules",
	};

	public string AbsPath { get; } = absPath;
	public bool IsDirectory { get; } = isDirectory;
	public string Title { get; } = Path.GetFileName(absPath);

	public Action<FsNode>? Activated { get; init; }

	public override object Text => Title;
	public override object Icon => IsDirectory ? Images.FolderClosed : Images.Document;
	public override object? ExpandedIcon => IsDirectory ? Images.FolderOpen : null;
	public override object ToolTip => AbsPath;
	public override bool ShowExpander => IsDirectory && base.ShowExpander;

	public override void ActivateItem(Stampeded.Core.TreeView.PlatformAbstractions.IPlatformRoutedEventArgs e)
	{
		Activated?.Invoke(this);
		e.Handled = true;
	}

	// A directory enumerates one level ahead of what is visible rather than the whole
	// worktree up front; the node model drives this through LazyLoading.
	protected override void LoadChildren()
	{
		if (!IsDirectory)
			return;
		try
		{
			foreach (var dir in Directory.EnumerateDirectories(AbsPath).Order(StringComparer.OrdinalIgnoreCase))
			{
				if (!SkippedDirectories.Contains(Path.GetFileName(dir)))
					Children.Add(new FsNode(dir, isDirectory: true) { LazyLoading = true, Activated = Activated });
			}
			foreach (var file in Directory.EnumerateFiles(AbsPath).Order(StringComparer.OrdinalIgnoreCase))
				Children.Add(new FsNode(file, isDirectory: false) { Activated = Activated });
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
		}
	}

	/// <summary>Expands to a repo-relative path and returns its node. The tree is a flat
	/// list, so revealing is walking the model - no container to materialize per level.</summary>
	public FsNode? Reveal(string relPath)
	{
		var node = this;
		foreach (var segment in relPath.Split('/'))
		{
			node.EnsureLazyChildren();
			node.IsExpanded = true;
			if (node.Children.OfType<FsNode>().FirstOrDefault(c => c.Title == segment) is not { } child)
				return null;
			node = child;
		}
		return node;
	}
}

/// <summary>
/// Full directory tree of the review's head worktree, so any file - changed or not - can
/// be opened as a navigable source document (semantic highlighting, go to definition,
/// find references all work there).
/// </summary>
public partial class FileBrowserPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	string? currentRoot;

	/// <summary>The worktree directory itself, hidden; its children are the visible rows.</summary>
	[ObservableProperty]
	SharpTreeNode? root;

	FsNode? rootNode;
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
		Root = null;
		rootNode = null;
		if (root is null || !Directory.Exists(root))
		{
			State.Status = "Open a review to browse its head worktree.";
			return;
		}
		rootNode = new FsNode(root, isDirectory: true) { LazyLoading = true, Activated = Open };
		Root = rootNode;
		State.Status = $"Head worktree: {root}. Double-click a file to open it.";
	}

	/// <summary>Expands to and selects a repo-relative file, for selection sync.</summary>
	public FsNode? Reveal(string relPath) => rootNode?.Reveal(relPath);

	void Open(FsNode node)
	{
		if (node.IsDirectory || workspace.WorktreePath is not { } root)
			return;
		string rel = Path.GetRelativePath(root, node.AbsPath).Replace('\\', '/');
		workspace.NavigateToFileLineAsync(rel, 1, oldSide: false, record: true).HandleExceptions();
	}
}
