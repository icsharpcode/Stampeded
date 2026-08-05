using System.Collections.ObjectModel;

using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.TreeView;

namespace Stampeded.Panes;

public sealed partial class ChangeMapState : ObservableObject
{
	[ObservableProperty]
	string status = "The change map lists changed members once semantics are loaded.";
}

public sealed class MapNode : SharpTreeNode
{
	readonly string title;
	readonly IImage? icon;
	readonly IBrush foreground;

	public MapNode(string title, IBrush? foreground, ReviewWorkspace.ChangeMapEntry? entry, IImage? icon)
	{
		this.title = title;
		this.icon = icon;
		this.foreground = foreground ?? Brushes.Gray;
		Entry = entry;
	}

	public ReviewWorkspace.ChangeMapEntry? Entry { get; }

	public Action<MapNode>? Activated { get; init; }

	public override object Text => title;
	public override object? Icon => icon;
	public override object Foreground => foreground;

	public override void ActivateItem(Stampeded.Core.TreeView.PlatformAbstractions.IPlatformRoutedEventArgs e)
	{
		Activated?.Invoke(this);
		e.Handled = true;
	}
}

/// <summary>
/// Symbol-level inventory of the diff as a tree (project > file > members), colored by
/// change kind: green added, blue modified, red removed. The design-stage anchor of the
/// review guide; clicking a member jumps to it.
/// </summary>
public partial class ChangeMapPaneViewModel : Tool
{
	static readonly IBrush Added = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush Modified = new SolidColorBrush(Color.Parse("#3794FF"));
	static readonly IBrush Removed = new SolidColorBrush(Color.Parse("#F85149"));

	readonly ReviewWorkspace workspace;

	/// <summary>Invisible parent of the project rows; SharpTreeView takes one root.</summary>
	[ObservableProperty]
	SharpTreeNode? root;
	public ChangeMapState State { get; } = new();

	public ChangeMapPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ChangeMapChanged += () => Dispatcher.UIThread.Post(Rebuild);
	}

	static IBrush BrushFor(string kind) => kind switch {
		"Added" => Added,
		"Removed" => Removed,
		_ => Modified,
	};

	void Rebuild()
	{
		var root = new MapNode("", null, null, null);
		var map = workspace.ChangeMap;
		foreach (var projectGroup in map.GroupBy(e => e.Project))
		{
			var projectNode = new MapNode(projectGroup.Key, null, null, Images.Assembly) { IsExpanded = true };
			foreach (var fileGroup in projectGroup.GroupBy(e => e.RelPath))
			{
				var fileNode = new MapNode(Path.GetFileName(fileGroup.Key), null, null, Images.Document) { IsExpanded = true };
				foreach (var entry in fileGroup.OrderBy(e => e.Line))
				{
					fileNode.Children.Add(new MapNode(entry.Display, BrushFor(entry.Kind), entry,
						Images.ForMemberKind(entry.MemberKind)) { Activated = Open });
				}
				projectNode.Children.Add(fileNode);
			}
			root.Children.Add(projectNode);
		}
		Root = root;
		State.Status = map.Count == 0
			? "No changed members mapped (semantics still loading, or non-C# changes only)."
			: $"{map.Count} changed member(s): green added, blue modified, red removed. Click to jump.";
	}

	void Open(MapNode node)
	{
		if (node.Entry is { } entry)
			workspace.NavigateToFileLineAsync(entry.RelPath, entry.Line, entry.OldSide, record: true).HandleExceptions();
	}
}
