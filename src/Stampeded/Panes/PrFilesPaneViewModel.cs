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

	bool testsFirst = true;
	public bool TestsFirst {
		get => testsFirst;
		set {
			testsFirst = value;
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
		// the list is - so marking them all says nothing, and ordering by it orders nothing.
		// The mark belongs to the whole change, where it picks out the few that moved.
		bool markTouched = !workspace.Scopes.InSinceLastPass;
		var ordered = workspace.Files
			// Generator output goes last whatever else is true of it: it is what the change
			// caused, and reaching the cause should never mean scrolling past the effect.
			.OrderBy(f => f.IsGenerated ? 1 : 0)
			.ThenBy(f => markTouched && workspace.IsTouchedSinceLastPass(f.Path) ? 0 : 1)
			.ThenBy(f => Core.Review.TestPaths.IsTestPath(f.Path) == testsFirst ? 0 : 1)
			.ThenBy(f => f.Path, StringComparer.Ordinal);
		foreach (var file in ordered)
		{
			int added = file.Hunks.Sum(h => h.Lines.Count(l => l.Kind == Core.Diff.PatchLineKind.Added));
			int removed = file.Hunks.Sum(h => h.Lines.Count(l => l.Kind == Core.Diff.PatchLineKind.Removed));
			var entry = new FileEntry(file, workspace.Store.IsViewed(file.Path)) {
				AddedText = added > 0 ? $"+{added}" : "",
				RemovedText = removed > 0 ? $"-{removed}" : "",
				CommentBadge = CommentBadgeFor(file.Path),
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
	string CommentBadgeFor(string path)
	{
		int count = workspace.Comments.Drafts.Count(d => d.Stored.Anchor.Path == path)
			+ workspace.Comments.Posted.Count(p => p.RelPath == path);
		return count > 0 ? $"{count} \u25cf" : "";
	}

	void RefreshCommentBadges()
	{
		foreach (var entry in Files)
			entry.CommentBadge = CommentBadgeFor(entry.File.Path);
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
