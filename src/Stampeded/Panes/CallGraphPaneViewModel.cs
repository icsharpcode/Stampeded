using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Roslyn;
using Stampeded.Core.TreeView;
using Stampeded.Core.TreeView.PlatformAbstractions;

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
/// One member in the call tree. Children load on first expansion - a caller tree over a
/// widely used member is effectively unbounded, and the flattened tree lets it be walked
/// as deep as the reader cares to go.
/// </summary>
public sealed class CallGraphTreeNode : SharpTreeNode
{
	readonly CallGraphPaneViewModel owner;
	readonly ReviewWorkspace.CallGraphItem item;

	public CallGraphTreeNode(CallGraphPaneViewModel owner, ReviewWorkspace.CallGraphItem item)
	{
		this.owner = owner;
		this.item = item;
		LazyLoading = item.CanExpand;
	}

	public ReviewWorkspace.CallGraphItem Item => item;

	public override object Text => item.Detail.Length > 0 ? $"{item.Display}   {item.Detail}" : item.Display;

	public override object Icon => Images.Method;

	public override object ToolTip => item.RelPath is { Length: > 0 } path
		? $"{item.Node.Display}\n{path}:{item.Node.Line}"
		: item.Node.Display;

	protected override void LoadChildren()
	{
		// The lookup is asynchronous; a placeholder keeps the expander open meanwhile,
		// since an empty child list would collapse the node again.
		var placeholder = new PlaceholderNode("finding...");
		Children.Add(placeholder);
		LoadAsync().HandleExceptions();

		async Task LoadAsync()
		{
			var calls = await owner.GetCallsAsync(item);
			Dispatcher.UIThread.Post(() => {
				Children.Clear();
				foreach (var call in calls)
					Children.Add(new CallGraphTreeNode(owner, call));
				if (Children.Count == 0)
					Children.Add(new PlaceholderNode(owner.EmptyMessage));
			});
		}
	}

	public override void ActivateItem(IPlatformRoutedEventArgs e)
	{
		owner.Open(this);
		e.Handled = true;
	}
}

/// <summary>A non-navigable row: the pending or empty state of an expansion.</summary>
public sealed class PlaceholderNode(string text) : SharpTreeNode
{
	public override object Text => text;
}

/// <summary>
/// The call hierarchy around a symbol. Callers answer the question a change raises - who
/// depends on this - and callees answer how the member does its work.
/// </summary>
public partial class CallGraphPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	ReviewWorkspace.CallRoot? currentRoot;

	public CallGraphState State { get; } = new();

	/// <summary>The tree's root node; the view binds its <c>Root</c> to this.</summary>
	[ObservableProperty]
	SharpTreeNode? root;

	public CallGraphPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.CallGraphRequested += r => Dispatcher.UIThread.Post(() => SetRoot(r));
		workspace.CallGraphFailed += message => Dispatcher.UIThread.Post(() => {
			Root = null;
			State.Status = message;
		});
		workspace.ReviewChanged += () => Dispatcher.UIThread.Post(() => {
			currentRoot = null;
			Root = null;
			State.Status = "Right-click a symbol in a diff and choose Show Call Graph.";
		});
		State.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(CallGraphState.ShowCallers) && currentRoot is { } current)
				SetRoot(current);
		};
	}

	CallDirection Direction => State.ShowCallers ? CallDirection.Callers : CallDirection.Callees;

	public string EmptyMessage => State.ShowCallers ? "(no callers in this solution)" : "(no calls out)";

	internal Task<IReadOnlyList<ReviewWorkspace.CallGraphItem>> GetCallsAsync(ReviewWorkspace.CallGraphItem item)
		=> item.RelPath is { Length: > 0 } relPath
			? workspace.GetCallsAsync(relPath, item.Node.Line, item.Node.Column, item.OldSide, Direction)
			: Task.FromResult<IReadOnlyList<ReviewWorkspace.CallGraphItem>>([]);

	void SetRoot(ReviewWorkspace.CallRoot value)
	{
		currentRoot = value;
		State.Status = $"{(State.ShowCallers ? "Callers of" : "Calls from")} {value.Display}. "
			+ "Expand for the next level; double-click to jump.";
		var item = new ReviewWorkspace.CallGraphItem(
			new CallNode(value.Display, "", value.RelPath, value.Line, value.Column, 0),
			value.RelPath, value.OldSide);
		var node = new CallGraphTreeNode(this, item);
		Root = node;
		node.IsExpanded = true;
	}

	public void Open(CallGraphTreeNode node)
	{
		if (node.Item is { RelPath: { Length: > 0 } path } item)
			workspace.NavigateToFileLineAsync(path, item.Node.Line, item.OldSide, record: true).HandleExceptions();
	}
}
