using System.Collections.ObjectModel;

using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Documents;

namespace Stampeded.Panes;

public sealed record StructureNode(IBrush Foreground, FontWeight Weight, string Title, string RelPath, int BlobLine, bool OldSide)
{
	public ObservableCollection<StructureNode> Children { get; } = [];
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
public class StructurePaneViewModel : Tool
{
	static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush ModifiedBrush = new SolidColorBrush(Color.Parse("#3794FF"));

	readonly ReviewWorkspace workspace;
	string? currentPath;

	public ObservableCollection<StructureNode> Roots { get; } = [];
	public StructureState State { get; } = new();

	public StructurePaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		DiffDocumentView.ActiveViewChanged += () => Dispatcher.UIThread.Post(Rebuild);
		workspace.ReviewChanged += () => Dispatcher.UIThread.Post(() => {
			currentPath = null;
			Roots.Clear();
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
		Roots.Clear();
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
		foreach (var node in Core.Roslyn.DocumentOutline.Compute(sideText))
			Roots.Add(Convert(node, relPath, oldSide, changedSideLines));
		State.Status = $"{Path.GetFileName(vm.File.Path)}: green fully added, blue touched by the change. Double-click to jump.";
	}

	StructureNode Convert(Core.Roslyn.OutlineNode node, string relPath, bool oldSide, SortedSet<int> changed)
	{
		int changedInRange = changed.GetViewBetween(node.StartLine, node.EndLine).Count;
		int range = node.EndLine - node.StartLine + 1;
		var brush = changedInRange == 0 ? Brushes.Gray
			: changedInRange >= range ? AddedBrush
			: ModifiedBrush;
		var weight = node.Kind == "type" ? FontWeight.SemiBold : FontWeight.Normal;
		var result = new StructureNode(
			changedInRange == 0 && node.Kind == "type" ? Brushes.Gray : brush,
			weight, node.Title, relPath, node.StartLine, oldSide);
		foreach (var child in node.Children)
			result.Children.Add(Convert(child, relPath, oldSide, changed));
		return result;
	}

	public void Open(StructureNode node)
		=> workspace.NavigateToFileLineAsync(node.RelPath, node.BlobLine, node.OldSide, record: true).HandleExceptions();
}
