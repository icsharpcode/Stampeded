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
public class StampededDockFactory : Factory
{
	public override IRootDock CreateLayout()
	{
		var welcome = new WelcomeDocumentViewModel { Id = "Welcome", Title = "Welcome" };
		var documents = new DocumentDock {
			Id = "Documents",
			IsCollapsable = false,
			VisibleDockables = CreateList<IDockable>(welcome),
			ActiveDockable = welcome,
		};

		var prList = new PrListPaneViewModel { Id = "PullRequests", Title = "Pull Requests" };
		var leftDock = new ToolDock {
			Id = "LeftPane",
			Alignment = Alignment.Left,
			Proportion = 0.2,
			VisibleDockables = CreateList<IDockable>(prList),
			ActiveDockable = prList,
		};

		var mainLayout = new ProportionalDock {
			Orientation = Orientation.Horizontal,
			VisibleDockables = CreateList<IDockable>(
				leftDock,
				new ProportionalDockSplitter(),
				documents),
		};

		var root = CreateRootDock();
		root.Id = "Root";
		root.VisibleDockables = CreateList<IDockable>(mainLayout);
		root.ActiveDockable = mainLayout;
		root.DefaultDockable = mainLayout;
		return root;
	}
}
