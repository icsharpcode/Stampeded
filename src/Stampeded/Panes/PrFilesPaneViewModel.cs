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

	string coverageBadge = "";
	public string CoverageBadge {
		get => coverageBadge;
		set => SetProperty(ref coverageBadge, value);
	}

	string depth = "";
	/// <summary>Planned review depth: "deep", "skim", "trust" or "" (unplanned).</summary>
	public string Depth {
		get => depth;
		set {
			if (SetProperty(ref depth, value))
				OnPropertyChanged(nameof(DepthBadge));
		}
	}

	/// <summary>"new!" when the latest push touched this file after it was last reviewed.</summary>
	public string SinceBadge { get; init; } = "";

	public string DepthBadge => Depth switch {
		"deep" => "deep",
		"skim" => "skim",
		"trust" => "trust",
		_ => "",
	};
}

/// <summary>
/// Changed files of the open review, with per-file viewed tracking.
/// </summary>
public class PrFilesPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	bool suppressViewedPersist;

	bool testsFirst;
	public bool TestsFirst {
		get => testsFirst;
		set {
			testsFirst = value;
			Rebuild();
		}
	}

	public ObservableCollection<FileEntry> Files { get; } = [];

	public PrFilesPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ReviewChanged += Rebuild;
		workspace.ViewedChanged += OnViewedChanged;
		workspace.CoverageChanged += RefreshCoverageBadges;
		workspace.DepthChanged += RefreshDepths;
	}

	public void SetDepth(FileEntry entry, string depth)
		=> workspace.SetDepth(entry.File.Path, depth);

	void RefreshDepths()
	{
		foreach (var entry in Files)
			entry.Depth = workspace.GetDepth(entry.File.Path);
	}

	static int DepthRank(string depth) => depth switch {
		"deep" => 0,
		"skim" => 1,
		"" => 2,
		_ => 3, // trust reads last (or not at all)
	};

	void Rebuild()
	{
		Files.Clear();
		var ordered = workspace.Files
			.OrderBy(f => workspace.IsTouchedSinceLastPass(f.Path) ? 0 : 1)
			.ThenBy(f => Core.Review.TestPaths.IsTestPath(f.Path) == testsFirst ? 0 : 1)
			.ThenBy(f => DepthRank(workspace.GetDepth(f.Path)))
			.ThenBy(f => f.Path, StringComparer.Ordinal);
		foreach (var file in ordered)
		{
			var entry = new FileEntry(file, workspace.Store.IsViewed(file.Path)) {
				Depth = workspace.GetDepth(file.Path),
				SinceBadge = workspace.IsTouchedSinceLastPass(file.Path) ? "new!" : "",
			};
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
