using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Stampeded.Documents;

public partial class OverviewDocumentView : UserControl
{
	public OverviewDocumentView()
	{
		InitializeComponent();
	}

	OverviewDocumentViewModel? Vm => DataContext as OverviewDocumentViewModel;

	void OnBounce(object? sender, RoutedEventArgs e) => Vm?.Bounce();

	void OnRecord(object? sender, RoutedEventArgs e) => Vm?.OpenRecord();

	void OnIssueClick(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && (sender as Button)?.DataContext is IssueRef issue)
			vm.OpenIssue(issue);
	}

	void OnFileDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (Vm is { } vm && FilesList.SelectedItem is FileCostRow row)
			vm.OpenFileRow(row);
	}

	void OnImplDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (Vm is { } vm && ImplList.SelectedItem is MemberRow row)
			vm.OpenMember(row);
	}

	void OnTestDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (Vm is { } vm && TestList.SelectedItem is MemberRow row)
			vm.OpenMember(row);
	}

	void OnSweepDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (Vm is { } vm && SweepList.SelectedItem is ReviewWorkspace.SweepItem item)
			vm.OpenSweepItem(item);
	}
}
