using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

using Stampeded.Documents;
using Stampeded.Panes;

namespace Stampeded.Docking;

/// <summary>
/// Builds the default dock layout. Panes are wired directly here — this app has a small,
/// closed set of panes and no plugin story, so no registry indirection.
/// </summary>
public class StampededDockFactory(ReviewWorkspace workspace) : Factory
{
	public DocumentDock? Documents { get; private set; }

	readonly Dictionary<string, (Tool Pane, ToolDock Home)> panes = [];

	/// <summary>Brings a tool pane back: re-added to its home dock when it was closed
	/// (e.g. its floating window was dismissed), then activated and focused.</summary>
	public void ShowPane(string id)
	{
		if (!panes.TryGetValue(id, out var entry))
			return;
		if (entry.Home.VisibleDockables?.Contains(entry.Pane) != true
			&& FindDockable(entry.Pane) is null)
		{
			AddDockable(entry.Home, entry.Pane);
		}
		SetActiveDockable(entry.Pane);
		SetFocusedDockable(entry.Home, entry.Pane);
	}

	IDockable? FindDockable(IDockable dockable)
	{
		// Present anywhere in the layout (including floating windows) means no re-add.
		bool found = false;
		void Visit(IDock dock)
		{
			foreach (var child in dock.VisibleDockables ?? [])
			{
				if (child == dockable)
					found = true;
				else if (child is IDock nested)
					Visit(nested);
			}
		}
		if (RootDock is IDock root)
		{
			Visit(root);
			foreach (var window in (RootDock as IRootDock)?.Windows ?? [])
			{
				if (window.Layout is IDock layout)
					Visit(layout);
			}
		}
		return found ? dockable : null;
	}

	IRootDock? RootDock { get; set; }

	public override IRootDock CreateLayout()
	{
		Documents = new DocumentDock {
			Id = "Documents",
			IsCollapsable = false,
			VisibleDockables = CreateList<IDockable>(),
		};

		var explorer = new ExplorerPaneViewModel(workspace) { Id = "Explorer", Title = "Explorer" };
		var map = new ChangeMapPaneViewModel(workspace) { Id = "Map", Title = "Map" };
		var structure = new StructurePaneViewModel(workspace) { Id = "Structure", Title = "Structure" };
		var filesDock = new ToolDock {
			Id = "FilesDock",
			Alignment = Alignment.Left,
			VisibleDockables = CreateList<IDockable>(explorer, structure, map),
			ActiveDockable = explorer,
		};
		panes[explorer.Id] = (explorer, filesDock);
		panes[map.Id] = (map, filesDock);
		panes[structure.Id] = (structure, filesDock);
		var leftDock = new ProportionalDock {
			Proportion = 0.2,
			Orientation = Orientation.Vertical,
			VisibleDockables = CreateList<IDockable>(filesDock),
		};

		var references = new ReferencesPaneViewModel(workspace) { Id = "References", Title = "References" };
		var checks = new ChecksPaneViewModel(workspace) { Id = "Checks", Title = "Checks" };
		var tests = new TestsPaneViewModel(workspace) { Id = "Tests", Title = "Tests" };
		var comments = new CommentsPaneViewModel(workspace) { Id = "Comments", Title = "Comments" };
		workspace.CommentsPane = comments;
		var log = new LogPaneViewModel { Id = "Log", Title = "Log" };
		var run = new RunPaneViewModel(workspace) { Id = "Run", Title = "Run" };
		var commits = new CommitsPaneViewModel(workspace) { Id = "Commits", Title = "Commits" };
		var history = new HistoryPaneViewModel(workspace) { Id = "History", Title = "History" };
		var callGraph = new CallGraphPaneViewModel(workspace) { Id = "CallGraph", Title = "Call Graph" };
		var bottomDock = new ToolDock {
			Id = "BottomDock",
			Alignment = Alignment.Bottom,
			Proportion = 0.28,
			VisibleDockables = CreateList<IDockable>(references, callGraph, comments, commits, history, checks, tests, run, log),
			ActiveDockable = references,
		};
		foreach (var pane in new Tool[] { references, callGraph, comments, commits, history, checks, tests, run, log })
			panes[pane.Id!] = (pane, bottomDock);
		var rightSide = new ProportionalDock {
			Orientation = Orientation.Vertical,
			VisibleDockables = CreateList<IDockable>(
				Documents,
				new ProportionalDockSplitter(),
				bottomDock),
		};

		var mainLayout = new ProportionalDock {
			Orientation = Orientation.Horizontal,
			VisibleDockables = CreateList<IDockable>(
				leftDock,
				new ProportionalDockSplitter(),
				rightSide),
		};

		var root = CreateRootDock();
		root.Id = "Root";
		root.VisibleDockables = CreateList<IDockable>(mainLayout);
		root.ActiveDockable = mainLayout;
		root.DefaultDockable = mainLayout;
		RootDock = root;
		return root;
	}
}
