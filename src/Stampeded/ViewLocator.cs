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
		[typeof(WelcomeDocumentViewModel)] = () => new Documents.WelcomeDocumentView(),
		[typeof(DiffDocumentViewModel)] = () => new Documents.DiffDocumentView(),
		[typeof(PrListPaneViewModel)] = () => new Panes.PrListPaneView(),
		[typeof(PrFilesPaneViewModel)] = () => new Panes.PrFilesPaneView(),
		[typeof(ReferencesPaneViewModel)] = () => new Panes.ReferencesPaneView(),
		[typeof(ChecksPaneViewModel)] = () => new Panes.ChecksPaneView(),
		[typeof(TestsPaneViewModel)] = () => new Panes.TestsPaneView(),
		[typeof(CommentsPaneViewModel)] = () => new Panes.CommentsPaneView(),
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
