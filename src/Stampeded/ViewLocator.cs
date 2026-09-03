using Avalonia.Controls;
using Avalonia.Controls.Templates;

using Stampeded.Documents;
using Stampeded.Panes;

namespace Stampeded;

/// <summary>
/// Maps viewmodels to their views for Dock-hosted content. Explicit dictionary instead of
/// reflection: the set of dockables is small and closed, and a missing mapping should be
/// an obvious compile-adjacent failure, not a runtime name-convention miss.
/// </summary>
public class ViewLocator : IDataTemplate
{
	static readonly Dictionary<Type, Func<Control>> s_views = new() {
		[typeof(DiffDocumentViewModel)] = () => new Documents.DiffDocumentView(),
		[typeof(SideBySideDocumentViewModel)] = () => new Documents.SideBySideDocumentView(),
		[typeof(TextDocumentViewModel)] = () => new Documents.TextDocumentView(),
		[typeof(StartDocumentViewModel)] = () => new Documents.StartDocumentView(),
		[typeof(OverviewDocumentViewModel)] = () => new Documents.OverviewDocumentView(),
		[typeof(ReviewDocumentViewModel)] = () => new Documents.ReviewDocumentView(),
		[typeof(PrListPaneViewModel)] = () => new Panes.PrListPaneView(),
		[typeof(ExplorerPaneViewModel)] = () => new Panes.ExplorerPaneView(),
		[typeof(PrFilesPaneViewModel)] = () => new Panes.PrFilesPaneView(),
		[typeof(ReferencesPaneViewModel)] = () => new Panes.ReferencesPaneView(),
		[typeof(CallGraphPaneViewModel)] = () => new Panes.CallGraphPaneView(),
		[typeof(ChangeMapPaneViewModel)] = () => new Panes.ChangeMapPaneView(),
		[typeof(StructurePaneViewModel)] = () => new Panes.StructurePaneView(),
		[typeof(FileBrowserPaneViewModel)] = () => new Panes.FileBrowserPaneView(),
		[typeof(ChecksPaneViewModel)] = () => new Panes.ChecksPaneView(),
		[typeof(MergeQueuePaneViewModel)] = () => new Panes.MergeQueuePaneView(),
		[typeof(TestsPaneViewModel)] = () => new Panes.TestsPaneView(),
		[typeof(CommentsPaneViewModel)] = () => new Panes.CommentsPaneView(),
		[typeof(LogPaneViewModel)] = () => new Panes.LogPaneView(),
		[typeof(RunPaneViewModel)] = () => new Panes.RunPaneView(),
		[typeof(CommitsPaneViewModel)] = () => new Panes.CommitsPaneView(),
		[typeof(HistoryPaneViewModel)] = () => new Panes.HistoryPaneView(),
	};

	public Control Build(object? data)
	{
		if (data is not null && s_views.TryGetValue(data.GetType(), out var factory))
			return factory();
		return new TextBlock { Text = $"No view registered for {data?.GetType().Name ?? "(null)"}" };
	}

	public bool Match(object? data)
	{
		return data is not null && s_views.ContainsKey(data.GetType());
	}
}
