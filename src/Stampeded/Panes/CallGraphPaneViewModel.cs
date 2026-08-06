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

	/// <summary>Limits each level to members the review itself changes - the ones a change
	/// here can actually collide with.</summary>
	[ObservableProperty]
	bool changedOnly;
}

/// <summary>
/// A member in the call tree. Its children are the two directions - who calls it and what
/// it calls - so either can be followed at any depth without losing the tree, plus the
/// individual sites where it calls its parent when there is more than one.
/// </summary>
public sealed class CallMemberNode : SharpTreeNode
{
	readonly CallGraphPaneViewModel owner;

	public CallMemberNode(CallGraphPaneViewModel owner, ReviewWorkspace.CallGraphItem item)
	{
		this.owner = owner;
		Item = item;
		// Without this the node never asks for its children, so the direction buckets
		// under it are never built.
		LazyLoading = item.CanExpand;
	}

	public ReviewWorkspace.CallGraphItem Item { get; }

	public override object Text => Item.Detail.Length > 0
		? $"{Item.Display}   {Item.Detail}"
		: Item.Display;

	public override object Icon => Images.Method;

	public override object ToolTip => Item.RelPath is { Length: > 0 } path
		? $"{Item.Node.Display}\n{path}:{Item.Node.Line}"
		: Item.Node.Display;

	public override bool ShowExpander => Item.CanExpand;

	protected override void LoadChildren()
	{
		if (!Item.CanExpand)
			return;
		Children.Add(new CallBucketNode(owner, Item, CallDirection.Callers));
		Children.Add(new CallBucketNode(owner, Item, CallDirection.Callees));
		// The member row goes to the definition, so the calls themselves need rows of
		// their own - which one matters is exactly the reviewer's question.
		foreach (var site in Item.Sites)
			Children.Add(new CallSiteNode(owner, site));
	}

	public override void ActivateItem(IPlatformRoutedEventArgs e)
	{
		owner.OpenMember(this);
		e.Handled = true;
	}
}

/// <summary>One direction under a member: expanding it runs the lookup.</summary>
public sealed class CallBucketNode : SharpTreeNode
{
	readonly CallGraphPaneViewModel owner;
	readonly ReviewWorkspace.CallGraphItem item;
	readonly CallDirection direction;

	public CallBucketNode(CallGraphPaneViewModel owner, ReviewWorkspace.CallGraphItem item, CallDirection direction)
	{
		this.owner = owner;
		this.item = item;
		this.direction = direction;
		LazyLoading = true;
	}

	public override object Text => direction == CallDirection.Callers
		? $"Calls to '{item.Node.Display}'"
		: $"Calls from '{item.Node.Display}'";

	public override object Icon => direction == CallDirection.Callers ? Images.SubTypes : Images.SuperTypes;

	protected override void LoadChildren()
	{
		// The lookup is asynchronous; a placeholder holds the expander open, since an
		// empty child list would collapse the node again before the answer arrives.
		Children.Add(new PlaceholderNode("finding..."));
		LoadAsync().HandleExceptions();

		async Task LoadAsync()
		{
			var calls = await owner.GetCallsAsync(item, direction);
			Dispatcher.UIThread.Post(() => {
				Children.Clear();
				foreach (var call in calls)
					Children.Add(new CallMemberNode(owner, call));
				if (Children.Count == 0)
				{
					Children.Add(new PlaceholderNode(owner.State.ChangedOnly
						? "(none among the members this review changes)"
						: direction == CallDirection.Callers
							? "(no callers in this solution)"
							: "(no calls out)"));
				}
			});
		}
	}
}

/// <summary>One call: the place the member above it calls its parent, with the source
/// line, so a member that calls the target several times can be opened at the right one
/// rather than at its own signature.</summary>
public sealed class CallSiteNode(CallGraphPaneViewModel owner, ReviewWorkspace.CallSiteItem site) : SharpTreeNode
{
	public ReviewWorkspace.CallSiteItem Site { get; } = site;

	public override object Text => $"{Site.RelPath}:{Site.Line}   {Site.Preview}";

	public override object Icon => Images.ViewCode;

	public override object ToolTip => $"Call at {Site.RelPath}:{Site.Line}";

	public override void ActivateItem(IPlatformRoutedEventArgs e)
	{
		owner.OpenSite(Site);
		e.Handled = true;
	}
}

/// <summary>A non-navigable row: the pending or empty state of an expansion.</summary>
public sealed class PlaceholderNode(string text) : SharpTreeNode
{
	public override object Text => text;
}

/// <summary>
/// The call hierarchy around a symbol, in both directions at every level. Callers answer
/// the question a change raises - who depends on this - and callees answer how the member
/// does its work.
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
		// Children are fetched once per node, so changing what counts as a child has to
		// start the tree over.
		State.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(CallGraphState.ChangedOnly) && currentRoot is { } root)
				SetRoot(root);
		};
	}

	internal async Task<IReadOnlyList<ReviewWorkspace.CallGraphItem>> GetCallsAsync(
		ReviewWorkspace.CallGraphItem item, CallDirection direction)
	{
		if (item.RelPath is not { Length: > 0 } relPath)
			return [];
		var calls = await workspace.GetCallsAsync(
			relPath, item.Node.Line, item.Node.Column, item.OldSide, direction);
		return State.ChangedOnly ? [.. calls.Where(c => c.IsChanged)] : calls;
	}

	void SetRoot(ReviewWorkspace.CallRoot value)
	{
		currentRoot = value;
		State.Status = $"{value.Display}: expand either direction, at any level. Double-click to jump.";
		var item = new ReviewWorkspace.CallGraphItem(
			new CallNode(value.Display, "", value.RelPath, value.Line, value.Column, []),
			value.RelPath, value.OldSide, IsChanged: false, []);
		var node = new CallMemberNode(this, item);
		var hidden = new PlaceholderNode("");
		hidden.Children.Add(node);
		Root = hidden;
		node.IsExpanded = true;
	}

	/// <summary>Goes to the member's own definition. Where it calls its parent is a
	/// separate question, answered by the call rows underneath it.</summary>
	public void OpenMember(CallMemberNode node)
	{
		if (node.Item is { RelPath: { Length: > 0 } path } item)
			workspace.NavigateToFileLineAsync(path, item.Node.Line, item.OldSide, record: true).HandleExceptions();
	}

	public void OpenSite(ReviewWorkspace.CallSiteItem site)
		=> workspace.NavigateToFileLineAsync(site.RelPath, site.Line, site.OldSide, record: true).HandleExceptions();
}
