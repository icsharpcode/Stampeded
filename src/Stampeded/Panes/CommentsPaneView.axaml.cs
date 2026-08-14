using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Stampeded.Panes;

public partial class CommentsPaneView : UserControl
{
	public CommentsPaneView()
	{
		InitializeComponent();
	}

	CommentsPaneViewModel? Vm => DataContext as CommentsPaneViewModel;

	void OnAddDraft(object? s, RoutedEventArgs e) => Vm?.AddDraft();

	void OnSelectionChanged(object? s, SelectionChangedEventArgs e) => OpenSelected();

	void OnGoTo(object? s, RoutedEventArgs e) => OpenSelected();

	void OpenSelected()
	{
		if (Vm is { } vm && CommentList.SelectedItem is CommentRow row)
			vm.Open(row);
	}

	void OnOpenOnGitHub(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		if (DataContext is CommentsPaneViewModel vm && CommentList.SelectedItem is CommentRow row)
			vm.OpenOnGitHub(row);
	}

	void OnDelete(object? s, RoutedEventArgs e)
	{
		if (Vm is { } vm && CommentList.SelectedItem is CommentRow row)
			vm.RemoveSelected(row);
	}

	void OnRefresh(object? s, RoutedEventArgs e) => Vm?.Refresh();

	void OnApprove(object? s, RoutedEventArgs e) => Vm?.Submit("APPROVE");

	void OnRequestChanges(object? s, RoutedEventArgs e) => Vm?.Submit("REQUEST_CHANGES");

	void OnComment(object? s, RoutedEventArgs e) => Vm?.Submit("COMMENT");
}
