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

	public string Marker => File.Kind switch {
		FileChangeKind.Added => "A",
		FileChangeKind.Deleted => "D",
		FileChangeKind.Renamed => "R",
		_ => "M",
	};

	public string Display => File.Kind == FileChangeKind.Renamed
		? $"{File.OldPath} -> {File.NewPath}"
		: File.Path;

	bool isViewed = viewed;
	public bool IsViewed {
		get => isViewed;
		set => SetProperty(ref isViewed, value);
	}
}

/// <summary>
/// Changed files of the open review, with per-file viewed tracking.
/// </summary>
public class PrFilesPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	bool suppressViewedPersist;

	public ObservableCollection<FileEntry> Files { get; } = [];

	public PrFilesPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ReviewChanged += Rebuild;
		workspace.ViewedChanged += OnViewedChanged;
	}

	void Rebuild()
	{
		Files.Clear();
		var ordered = workspace.Files
			.OrderBy(f => GuidePaneViewModel.IsTestPath(f.Path) ? 1 : 0)
			.ThenBy(f => f.Path, StringComparer.Ordinal);
		foreach (var file in ordered)
		{
			var entry = new FileEntry(file, workspace.Store.IsViewed(file.Path));
			entry.PropertyChanged += OnEntryChanged;
			Files.Add(entry);
		}
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

	public void Open(FileEntry entry)
	{
		workspace.OpenFileAsync(entry.File).HandleExceptions();
	}
}
