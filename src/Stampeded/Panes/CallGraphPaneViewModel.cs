using System.Collections.ObjectModel;

using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Roslyn;

namespace Stampeded.Panes;

public sealed partial class CallGraphState : ObservableObject
{
	[ObservableProperty]
	string status = "Right-click a symbol in a diff and choose Show Call Graph.";

	/// <summary>True while listing callers; false while listing callees.</summary>
	[ObservableProperty]
	bool showCallers = true;
}

/// <summary>
/// A node of the call tree. Children are loaded when the node is first expanded, so the
/// graph is only walked where the reader actually looks - a caller tree over a widely used
/// member is effectively unbounded otherwise.
/// </summary>
public sealed partial class CallGraphNode : ObservableObject
{
	readonly Func<CallGraphNode, Task> loadChildren;
	bool loaded;

	public CallGraphNode(string display, string detail, IImage? icon, ReviewWorkspace.CallGraphItem? item,
		Func<CallGraphNode, Task> loadChildren)
	{
		Display = display;
		Detail = detail;
		Icon = icon;
		Item = item;
		this.loadChildren = loadChildren;
		// A placeholder gives the node an expander before its children are known.
		if (item is null || item.CanExpand)
			Children.Add(Placeholder);
	}

	static CallGraphNode Placeholder { get; } = new("loading...", "", null, null, _ => Task.CompletedTask);

	public string Display { get; }
	public string Detail { get; }
	public IImage? Icon { get; }
	public ReviewWorkspace.CallGraphItem? Item { get; }
	public ObservableCollection<CallGraphNode> Children { get; } = [];

	[ObservableProperty]
	bool isExpanded;

	partial void OnIsExpandedChanged(bool value)
	{
		if (!value || loaded)
			return;
		loaded = true;
		loadChildren(this).HandleExceptions();
	}
}

/// <summary>
/// The call hierarchy around a symbol, as an expandable tree. Callers answer the question
/// a change raises - who depends on this - and callees answer how the member works.
/// </summary>
public class CallGraphPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	ReviewWorkspace.CallRoot? root;

	public ObservableCollection<CallGraphNode> Roots { get; } = [];
	public CallGraphState State { get; } = new();

	public CallGraphPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.CallGraphRequested += r => Dispatcher.UIThread.Post(() => SetRoot(r));
		workspace.CallGraphFailed += message => Dispatcher.UIThread.Post(() => {
			Roots.Clear();
			State.Status = message;
		});
		workspace.ReviewChanged += () => Dispatcher.UIThread.Post(() => {
			root = null;
			Roots.Clear();
			State.Status = "Right-click a symbol in a diff and choose Show Call Graph.";
		});
		State.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(CallGraphState.ShowCallers) && root is { } current)
				SetRoot(current);
		};
	}

	CallDirection Direction => State.ShowCallers ? CallDirection.Callers : CallDirection.Callees;

	void SetRoot(ReviewWorkspace.CallRoot value)
	{
		root = value;
		Roots.Clear();
		string what = State.ShowCallers ? "Callers of" : "Calls from";
		State.Status = $"{what} {value.Display}. Expand a node for the next level; double-click to jump.";
		var rootItem = new ReviewWorkspace.CallGraphItem(
			new CallNode(value.Display, "", value.RelPath, value.Line, value.Column, 0),
			value.RelPath, value.OldSide);
		var node = new CallGraphNode(value.Display, value.RelPath, Images.Method, rootItem, LoadChildrenAsync) {
			IsExpanded = true,
		};
		Roots.Add(node);
	}

	async Task LoadChildrenAsync(CallGraphNode node)
	{
		if (node.Item is not { RelPath: { Length: > 0 } relPath } item)
			return;
		var calls = await workspace.GetCallsAsync(
			relPath, item.Node.Line, item.Node.Column, item.OldSide, Direction);
		node.Children.Clear();
		foreach (var call in calls)
		{
			node.Children.Add(new CallGraphNode(
				call.Display, call.Detail, Images.Method, call, LoadChildrenAsync));
		}
		if (node.Children.Count == 0)
		{
			State.Status = State.ShowCallers
				? $"{node.Display}: no callers in this solution."
				: $"{node.Display}: no calls out.";
		}
	}

	public void Open(CallGraphNode node)
	{
		if (node.Item is { RelPath: { Length: > 0 } path } item)
			workspace.NavigateToFileLineAsync(path, item.Node.Line, item.OldSide, record: true).HandleExceptions();
	}
}
