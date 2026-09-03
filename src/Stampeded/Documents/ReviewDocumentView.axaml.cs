using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Stampeded.Documents;

public partial class ReviewDocumentView : UserControl
{
	public ReviewDocumentView()
	{
		InitializeComponent();
	}

	ReviewDocumentViewModel? Vm => DataContext as ReviewDocumentViewModel;

	void OnRefresh(object? s, RoutedEventArgs e) => Vm?.Refresh();

	void OnApprove(object? s, RoutedEventArgs e) => Vm?.Submit("APPROVE");

	void OnRequestChanges(object? s, RoutedEventArgs e) => Vm?.Submit("REQUEST_CHANGES");

	void OnComment(object? s, RoutedEventArgs e) => Vm?.Submit("COMMENT");

	void OnMarkReady(object? s, RoutedEventArgs e) => Vm?.MarkReadyForReview();

	void OnMerge(object? s, RoutedEventArgs e) => Vm?.Merge();

	void OnOpen(object? s, TappedEventArgs e)
	{
		if (Vm is { } vm && CommentList.SelectedItem is ReviewCommentRow row)
			vm.Open(row);
	}
}
