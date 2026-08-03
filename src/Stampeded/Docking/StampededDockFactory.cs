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

	public override IRootDock CreateLayout()
	{
		Documents = new DocumentDock {
			Id = "Documents",
			IsCollapsable = false,
			VisibleDockables = CreateList<IDockable>(),
		};

		var prList = new PrListPaneViewModel(workspace) { Id = "PullRequests", Title = "Pull Requests" };
		var files = new PrFilesPaneViewModel(workspace) { Id = "Files", Title = "Files" };
		var leftDock = new ProportionalDock {
			Proportion = 0.22,
			Orientation = Orientation.Vertical,
			VisibleDockables = CreateList<IDockable>(
				new ToolDock {
					Id = "PrListDock",
					Alignment = Alignment.Left,
					Proportion = 0.45,
					VisibleDockables = CreateList<IDockable>(prList),
					ActiveDockable = prList,
				},
				new ProportionalDockSplitter(),
				new ToolDock {
					Id = "FilesDock",
					Alignment = Alignment.Left,
					VisibleDockables = CreateList<IDockable>(files),
					ActiveDockable = files,
				}),
		};

		var references = new ReferencesPaneViewModel(workspace) { Id = "References", Title = "References" };
		var checks = new ChecksPaneViewModel(workspace) { Id = "Checks", Title = "Checks" };
		var tests = new TestsPaneViewModel(workspace) { Id = "Tests", Title = "Tests" };
		var comments = new CommentsPaneViewModel(workspace) { Id = "Comments", Title = "Comments" };
		workspace.CommentsPane = comments;
		var rightSide = new ProportionalDock {
			Orientation = Orientation.Vertical,
			VisibleDockables = CreateList<IDockable>(
				Documents,
				new ProportionalDockSplitter(),
				new ToolDock {
					Id = "BottomDock",
					Alignment = Alignment.Bottom,
					Proportion = 0.28,
					VisibleDockables = CreateList<IDockable>(references, comments, checks, tests),
					ActiveDockable = references,
				}),
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
		return root;
	}
}
