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

	[ObservableProperty]
	Documents.StartDocumentViewModel? startPage;

	/// <summary>Scale of the whole window's content. Steps are multiplicative so each press
	/// changes the size by as much as the last one appeared to.</summary>
	[ObservableProperty]
	double zoom = 1.0;

	const double ZoomStep = 1.1;
	const double MinZoom = 0.5;
	const double MaxZoom = 3.0;

	public void ZoomIn() => Zoom = Math.Min(MaxZoom, Zoom * ZoomStep);

	public void ZoomOut() => Zoom = Math.Max(MinZoom, Zoom / ZoomStep);

	public void ZoomReset() => Zoom = 1.0;

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
		workspace.OpenStart();
		StartPage = workspace.StartPage;
		workspace.ReviewChanged += UpdateTitle;
		UpdateTitle();
		RecentRepos.Record(Program.RepoPath);
	}

	void UpdateTitle()
	{
		string repo = $"{Path.GetFileName(Program.RepoPath)}  ({Program.RepoPath})";
		WindowTitle = App.Workspace switch {
			{ CurrentPr: { } pr } => $"Stampeded!  -  {repo}  -  PR #{pr.Number}",
			{ LocalRange: { } range } => $"Stampeded!  -  {repo}  -  {range.Head}",
			_ => $"Stampeded!  -  {repo}",
		};
	}
}
