using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Controls;

using Stampeded.Docking;

namespace Stampeded;

public partial class MainViewModel : ObservableObject
{
	public IRootDock Layout { get; }

	public BusyTracker Busy { get; }

	public ObservableCollection<string> Recent { get; } = new(RecentRepos.Load());

	[ObservableProperty]
	string windowTitle = "Stampeded!";

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
		workspace.OpenWizard();
		workspace.ReviewChanged += UpdateTitle;
		UpdateTitle();
		RecentRepos.Record(Program.RepoPath);
	}

	void UpdateTitle()
	{
		string repo = $"{Path.GetFileName(Program.RepoPath)}  ({Program.RepoPath})";
		WindowTitle = App.Workspace?.CurrentPr is { } pr
			? $"Stampeded!  -  {repo}  -  PR #{pr.Number}"
			: $"Stampeded!  -  {repo}";
	}
}
