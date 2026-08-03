using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

namespace Stampeded.Panes;

public sealed partial class ChangeMapState : ObservableObject
{
	[ObservableProperty]
	string status = "The change map lists changed members once semantics are loaded.";
}

public sealed record ChangeMapRow(string Marker, string Display, bool IsHeader, ReviewWorkspace.ChangeMapEntry? Entry)
{
	public string Text => IsHeader ? Display : $"{Marker} {Display}";
}

/// <summary>
/// Symbol-level inventory of the diff: judge the change at member granularity (which
/// methods/types were touched or removed) and jump to any of them - the design-stage
/// anchor of the review guide.
/// </summary>
public class ChangeMapPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;

	public ObservableCollection<ChangeMapRow> Rows { get; } = [];
	public ChangeMapState State { get; } = new();

	public ChangeMapPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ChangeMapChanged += () => Dispatcher.UIThread.Post(Rebuild);
	}

	void Rebuild()
	{
		Rows.Clear();
		var map = workspace.ChangeMap;
		foreach (var projectGroup in map.GroupBy(e => e.Project))
		{
			Rows.Add(new ChangeMapRow("", $"== {projectGroup.Key} ==", true, null));
			foreach (var fileGroup in projectGroup.GroupBy(e => e.RelPath))
			{
				foreach (var entry in fileGroup.OrderBy(e => e.Line))
					Rows.Add(new ChangeMapRow(entry.Kind, entry.Display, false, entry));
			}
		}
		State.Status = map.Count == 0
			? "No changed members mapped (semantics still loading, or non-C# changes only)."
			: $"{map.Count} changed member(s). M = modified/added at head, R = removed. Click to jump.";
	}

	public void Open(ChangeMapRow row)
	{
		if (row.Entry is { } entry)
			workspace.NavigateToFileLineAsync(entry.RelPath, entry.Line, entry.OldSide, record: true).HandleExceptions();
	}
}
