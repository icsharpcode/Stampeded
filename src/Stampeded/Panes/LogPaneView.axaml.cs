using System.Collections.Specialized;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Stampeded.Panes;

public partial class LogPaneView : UserControl
{
	public LogPaneView()
	{
		InitializeComponent();
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (DataContext is LogPaneViewModel vm)
			vm.Lines.CollectionChanged += OnLinesChanged;
	}

	void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (e.Action == NotifyCollectionChangedAction.Add && List.ItemCount > 0)
			List.ScrollIntoView(List.ItemCount - 1);
	}

	void OnClear(object? sender, RoutedEventArgs e)
	{
		if (DataContext is LogPaneViewModel vm)
			vm.Clear();
	}
}
