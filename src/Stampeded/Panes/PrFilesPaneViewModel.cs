using System.Collections.ObjectModel;
using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Diff;
using Stampeded.Core.Infra;

namespace Stampeded.Panes;

public sealed class FileEntry(FileDiff file, bool viewed) : ObservableObject
{
	public FileDiff File { get; } = file;

	/// <summary>"G" for generator output, whose add/modify/delete matters far less than the
	/// fact that nobody wrote it.</summary>
	public string Marker => File.IsGenerated ? "G" : File.Kind switch {
		FileChangeKind.Added => "A",
		FileChangeKind.Deleted => "D",
		FileChangeKind.Renamed => "R",
		_ => "M",
	};

	public string Display => File.Kind == FileChangeKind.Renamed
		? $"{File.OldPath} -> {File.NewPath}"
		: File.Path;

	/// <summary>The name alone: in the tree the directory is the row above.</summary>
	public string Name => File.Kind == FileChangeKind.Renamed
		? $"{Path.GetFileName(File.OldPath)} -> {Path.GetFileName(File.NewPath)}"
		: Path.GetFileName(File.Path);

	bool isViewed = viewed;
	public bool IsViewed {
		get => isViewed;
		set => SetProperty(ref isViewed, value);
	}

	string coverageBadge = "";
	public string CoverageBadge {
		get => coverageBadge;
		set => SetProperty(ref coverageBadge, value);
	}

	/// <summary>"new!" when the latest push touched this file after it was last reviewed.</summary>
	public string SinceBadge { get; init; } = "";

	/// <summary>How much of the file changed, the way the diff counts it. The size of a file's
	/// change is what a reader picks the next one by, and the list was the one place that said
	/// only which files, not how much of them.</summary>
	public string AddedText { get; init; } = "";

	public string RemovedText { get; init; } = "";

	string comments = "";
	/// <summary>How many remarks stand on this file - drafts of this pass and comments already
	/// posted. A file that has been written about reads differently from one that has not, and
	/// finding out meant opening it.</summary>
	public string CommentBadge {
		get => comments;
		set => SetProperty(ref comments, value);
	}

	/// <summary>The colours of the comment mark: amber while something on the file is still
	/// open, green once it is not. A file with something outstanding and a file that has been
	/// answered both carry a count, and the count alone made them look alike.</summary>
	static readonly Avalonia.Media.IBrush Open = Avalonia.Media.Brush.Parse("#D29922");
	static readonly Avalonia.Media.IBrush Settled = Avalonia.Media.Brush.Parse("#2EA043");

	bool commentsSettled;
	/// <summary>Whether every remark on this file has been answered - each thread resolved and
	/// nothing still drafted.</summary>
	public bool CommentsSettled {
		get => commentsSettled;
		set {
			if (SetProperty(ref commentsSettled, value))
			{
				OnPropertyChanged(nameof(CommentColor));
				OnPropertyChanged(nameof(CommentTip));
			}
		}
	}

	public Avalonia.Media.IBrush CommentColor => commentsSettled ? Settled : Open;

	public string CommentTip => commentsSettled
		? "Comments on this file, all of them resolved"
		: "Comments on this file - drafts of this pass and comments already posted";
}

/// <summary>
/// A row of the changed-file tree: a file, or a directory of them. A run of directories that
/// each hold one directory is a single row ("src/Stampeded/Panes"), because a level with no
/// choice in it is a level nobody navigates - it only costs indentation and a click.
/// </summary>
public sealed class FileNode(string name)
{
	public string Name { get; private set; } = name;

	public List<FileNode> Children { get; private set; } = [];

	/// <summary>The file this row is, or null for a directory.</summary>
	public FileEntry? Entry { get; init; }

	public bool IsFile => Entry is not null;

	public string ToolTip => Entry?.Display ?? Name;

	/// <summary>Builds the tree over files in the order they should be read, so a directory
	/// appears where its first file would have and the order within one is unchanged.</summary>
	public static List<FileNode> Build(IEnumerable<FileEntry> files)
	{
		var roots = new List<FileNode>();
		foreach (var entry in files)
		{
			var segments = entry.File.Path.Split('/');
			var siblings = roots;
			for (int i = 0; i < segments.Length - 1; i++)
			{
				var directory = siblings.FirstOrDefault(n => !n.IsFile && n.Name == segments[i]);
				if (directory is null)
					siblings.Add(directory = new FileNode(segments[i]));
				siblings = directory.Children;
			}
			siblings.Add(new FileNode(segments[^1]) { Entry = entry });
		}
		Compact(roots);
		return roots;
	}

	static void Compact(List<FileNode> nodes)
	{
		foreach (var node in nodes)
		{
			Compact(node.Children);
			while (!node.IsFile && node.Children is [{ IsFile: false } only])
			{
				node.Name += "/" + only.Name;
				node.Children = only.Children;
			}
		}
	}
}

/// <summary>
/// Changed files of the open review, with per-file viewed tracking.
/// </summary>
public class PrFilesPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	bool suppressViewedPersist;

	/// <summary>The review's own setting, shown here because this is the list it reorders -
	/// and read from there, so the keys that walk the list walk it in the same order.</summary>
	public bool TestsFirst {
		get => workspace.TestsFirst;
		set {
			workspace.TestsFirst = value;
			Rebuild();
		}
	}

	/// <summary>Every changed file in reading order; the tree is this list, grouped.</summary>
	public ObservableCollection<FileEntry> Files { get; } = [];

	public ObservableCollection<FileNode> Roots { get; } = [];

	public PrFilesPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ReviewChanged += Rebuild;
		workspace.ViewedChanged += OnViewedChanged;
		workspace.CoverageChanged += RefreshCoverageBadges;
		// Drafts come and go during a pass without the file list being rebuilt.
		workspace.Comments.Changed += RefreshCommentBadges;
	}

	void Rebuild()
	{
		Files.Clear();
		// Every file in the since-last-pass scope changed since the last pass - that is what
		// the list is - so marking them all says nothing.
		bool markTouched = !workspace.Scopes.InSinceLastPass;
		foreach (var file in workspace.ReadingOrder)
		{
			int added = file.Hunks.Sum(h => h.Lines.Count(l => l.Kind == Core.Diff.PatchLineKind.Added));
			int removed = file.Hunks.Sum(h => h.Lines.Count(l => l.Kind == Core.Diff.PatchLineKind.Removed));
			var (badge, settled) = CommentBadgeFor(file.Path);
			var entry = new FileEntry(file, workspace.Store.IsViewed(file.Path)) {
				AddedText = added > 0 ? $"+{added}" : "",
				RemovedText = removed > 0 ? $"-{removed}" : "",
				CommentBadge = badge,
				CommentsSettled = settled,
				SinceBadge = markTouched && workspace.IsTouchedSinceLastPass(file.Path) ? "new!" : "",
			};
			entry.PropertyChanged += OnEntryChanged;
			Files.Add(entry);
		}
		Roots.Clear();
		foreach (var node in FileNode.Build(Files))
			Roots.Add(node);
	}

	void OnEntryChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (suppressViewedPersist || e.PropertyName != nameof(FileEntry.IsViewed) || sender is not FileEntry entry)
			return;
		workspace.SetViewed(entry.File.Path, entry.IsViewed);
	}

	void OnViewedChanged(string path, bool viewed)
	{
		var entry = Files.FirstOrDefault(f => f.File.Path == path);
		if (entry is null)
			return;
		suppressViewedPersist = true;
		entry.IsViewed = viewed;
		suppressViewedPersist = false;
	}

	/// <summary>The remarks standing on a file: drafts written in this pass and comments
	/// already posted, counted together - both are things said about it that a reader is about
	/// to meet.</summary>
	(string Badge, bool Settled) CommentBadgeFor(string path)
	{
		var drafts = workspace.Comments.Drafts.Where(d => d.Stored.Anchor.Path == path).ToList();
		var posted = workspace.Comments.Posted.Where(p => p.RelPath == path).ToList();
		int count = drafts.Count + posted.Count;
		// A draft has no resolution to have: it has not been posted, so nothing about it is
		// settled, and one of them is enough to keep the file's mark open.
		bool settled = count > 0 && drafts.Count == 0 && posted.All(p => p.IsResolved);
		return (count > 0 ? $"{count} \u25cf" : "", settled);
	}

	void RefreshCommentBadges()
	{
		foreach (var entry in Files)
			(entry.CommentBadge, entry.CommentsSettled) = CommentBadgeFor(entry.File.Path);
	}

	void RefreshCoverageBadges()
	{
		foreach (var entry in Files)
		{
			var (uncovered, measured) = workspace.UncoveredAddedForFile(entry.File.Path);
			entry.CoverageBadge = measured == 0 ? "" : uncovered > 0 ? $"{uncovered}!" : "cov";
		}
	}

	public void Open(FileEntry entry)
	{
		workspace.OpenFileAsync(entry.File).HandleExceptions();
	}
}
