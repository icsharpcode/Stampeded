using System.Collections.ObjectModel;

using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.TreeView;
using Stampeded.Documents;

namespace Stampeded.Panes;

/// <summary>
/// One outline entry. Tinting is carried on the node's own Foreground, which the shared
/// cell template binds; type rows are told apart by their icon rather than by weight,
/// since the flattened tree has one label style for every row.
/// </summary>
public sealed class StructureNode(
	Avalonia.Media.IImage? icon, IBrush foreground, string title, string relPath, int blobLine, bool oldSide)
	: SharpTreeNode
{
	public string RelPath { get; } = relPath;
	public int BlobLine { get; } = blobLine;
	public bool OldSide { get; } = oldSide;

	public override object Text => title;
	public override object? Icon => icon;
	public override object Foreground => foreground;
	public override object ToolTip => $"{RelPath}:{BlobLine}";

	public Action<StructureNode>? Activated { get; init; }

	public override void ActivateItem(Stampeded.Core.TreeView.PlatformAbstractions.IPlatformRoutedEventArgs e)
	{
		Activated?.Invoke(this);
		e.Handled = true;
	}
}

public sealed partial class StructureState : ObservableObject
{
	[ObservableProperty]
	string status = "The structure of the active document appears here.";
}

/// <summary>
/// Document outline of the active diff: every type and member, with members touched by
/// the change tinted like the map (green fully added, blue modified). Double-click
/// jumps; the tree follows the active document.
/// </summary>
public partial class StructurePaneViewModel : Tool
{
	static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush ModifiedBrush = new SolidColorBrush(Color.Parse("#3794FF"));

	readonly ReviewWorkspace workspace;
	string? currentPath;

	/// <summary>Invisible parent of the outline's top-level entries; SharpTreeView takes a
	/// single root and is told not to show it.</summary>
	[ObservableProperty]
	SharpTreeNode? root;
	public StructureState State { get; } = new();

	public StructurePaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		DiffDocumentView.ActiveViewChanged += () => Dispatcher.UIThread.Post(Rebuild);
		workspace.ReviewChanged += () => Dispatcher.UIThread.Post(() => {
			currentPath = null;
			Root = null;
			State.Status = "The structure of the active document appears here.";
		});
	}

	void Rebuild()
	{
		var view = DiffDocumentView.ActiveView;
		var vm = view?.ViewModel;
		if (vm is null)
			return;
		if (vm.File.Path == currentPath)
			return;
		currentPath = vm.File.Path;
		Root = null;
		if (!vm.File.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
		{
			State.Status = $"{Path.GetFileName(vm.File.Path)}: not a C# document.";
			return;
		}
		bool oldSide = vm.File.Kind == Core.Diff.FileChangeKind.Deleted;
		var (sideText, _) = vm.Model.GetSideText(oldSide);
		if (sideText.Length == 0)
			return;
		// Side lines touched by the change, for tinting members like the map.
		var changedSideLines = new SortedSet<int>();
		foreach (var tag in vm.Model.Tags)
		{
			if (!oldSide && tag.Kind == Core.Diff.DiffLineKind.Added && tag.NewLine > 0)
				changedSideLines.Add(tag.NewLine);
			else if (oldSide && tag.OldLine > 0)
				changedSideLines.Add(tag.OldLine);
		}
		string relPath = oldSide ? vm.File.OldPath : vm.File.Path;
		var root = new StructureNode(null, Brushes.Gray, "", relPath, 1, oldSide);
		foreach (var node in Core.Roslyn.DocumentOutline.Compute(sideText))
			root.Children.Add(Convert(node, relPath, oldSide, changedSideLines));
		Root = root;
		State.Status = $"{Path.GetFileName(vm.File.Path)}: green fully added, blue touched by the change. Double-click to jump.";
	}

	StructureNode Convert(Core.Roslyn.OutlineNode node, string relPath, bool oldSide, SortedSet<int> changed)
	{
		int changedInRange = changed.GetViewBetween(node.StartLine, node.EndLine).Count;
		int range = node.EndLine - node.StartLine + 1;
		var brush = changedInRange == 0 ? Brushes.Gray
			: changedInRange >= range ? AddedBrush
			: ModifiedBrush;
		bool isType = node.Kind is "class" or "struct" or "interface" or "record" or "enum";
		var result = new StructureNode(
			Images.ForOutlineKind(node.Kind),
			changedInRange == 0 && isType ? Brushes.Gray : brush,
			node.Title, relPath, node.StartLine, oldSide) {
			Activated = Open,
			IsExpanded = true,
		};
		foreach (var child in node.Children)
			result.Children.Add(Convert(child, relPath, oldSide, changed));
		return result;
	}

	void Open(StructureNode node)
		=> workspace.NavigateToFileLineAsync(node.RelPath, node.BlobLine, node.OldSide, record: true).HandleExceptions();
}
