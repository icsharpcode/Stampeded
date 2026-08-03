using Dock.Model.Controls;

using Stampeded.Docking;

namespace Stampeded;

public class MainViewModel
{
	public IRootDock Layout { get; }

	public BusyTracker Busy { get; }

	public MainViewModel()
	{
		var workspace = new ReviewWorkspace(Program.RepoPath);
		Busy = workspace.Busy;
		App.Workspace = workspace;
		var factory = new StampededDockFactory(workspace);
		Layout = factory.CreateLayout();
		factory.InitLayout(Layout);
		workspace.Factory = factory;
		workspace.Documents = factory.Documents;
	}
}
