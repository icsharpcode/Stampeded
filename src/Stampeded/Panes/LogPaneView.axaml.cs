using System.Collections.Specialized;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
		if (e.Action == NotifyCollectionChangedAction.Add && LogList.ItemCount > 0)
			LogList.ScrollIntoView(LogList.ItemCount - 1);
	}

	void OnClear(object? sender, RoutedEventArgs e)
	{
		if (DataContext is LogPaneViewModel vm)
			vm.Clear();
	}

	void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
		{
			CopySelected();
			e.Handled = true;
		}
	}

	void OnCopySelected(object? sender, RoutedEventArgs e) => CopySelected();

	void OnCopyAll(object? sender, RoutedEventArgs e)
	{
		if (DataContext is LogPaneViewModel vm)
			CopyToClipboard(string.Join('\n', vm.Lines));
	}

	void CopySelected()
	{
		if (DataContext is not LogPaneViewModel vm || LogList.SelectedItems is not { Count: > 0 } selected)
			return;
		// SelectedItems reflects selection order; copy in display order instead.
		var chosen = selected.OfType<string>().ToHashSet();
		CopyToClipboard(string.Join('\n', vm.Lines.Where(chosen.Contains)));
	}

	void CopyToClipboard(string text)
	{
		if (text.Length > 0)
			TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text).HandleExceptions();
	}
}
