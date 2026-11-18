using System.Collections.Specialized;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

using Stampeded.Controls;

namespace Stampeded.Panes;

public partial class LogPaneView : UserControl
{
	public LogPaneView()
	{
		InitializeComponent();
	}

	LogPaneViewModel? subscribed;

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (subscribed is not null)
			subscribed.VisibleLines.CollectionChanged -= OnLinesChanged;
		if (DataContext is LogPaneViewModel vm)
		{
			subscribed = vm;
			vm.VisibleLines.CollectionChanged += OnLinesChanged;
		}
		else
		{
			subscribed = null;
		}
	}

	void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (e.Action == NotifyCollectionChangedAction.Add && LogList.ItemCount > 0)
			LogList.ScrollRowIntoView(LogList.ItemCount - 1);
	}

	void OnClear(object? sender, RoutedEventArgs e)
	{
		if (DataContext is LogPaneViewModel vm)
			vm.Clear();
	}

	void OnToggleDetails(object? sender, RoutedEventArgs e)
	{
		if (sender is Button { DataContext: LogLineRow row })
			row.ToggleDetails();
	}

	void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
		{
			FilterBox.Focus();
			FilterBox.SelectAll();
			e.Handled = true;
			return;
		}
		if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
		{
			CopySelected();
			e.Handled = true;
		}
	}

	void OnFilterKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Escape || DataContext is not LogPaneViewModel vm)
			return;
		vm.FilterText = "";
		e.Handled = true;
	}

	void OnCopySelected(object? sender, RoutedEventArgs e) => CopySelected();

	void OnCopyAll(object? sender, RoutedEventArgs e)
	{
		if (DataContext is LogPaneViewModel vm)
			CopyToClipboard(string.Join('\n', vm.VisibleLines.Select(line => line.Text)));
	}

	void CopySelected()
	{
		if (DataContext is not LogPaneViewModel vm || LogList.SelectedItems is not { Count: > 0 } selected)
			return;
		// SelectedItems reflects selection order; copy in display order instead.
		var chosen = selected.OfType<LogLineRow>().ToHashSet();
		CopyToClipboard(string.Join('\n', vm.VisibleLines.Where(chosen.Contains).Select(line => line.Text)));
	}

	void CopyToClipboard(string text)
	{
		if (text.Length > 0)
			TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text).HandleExceptions();
	}
}
