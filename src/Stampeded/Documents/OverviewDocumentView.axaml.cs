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

	void OnOpenPr(object? sender, RoutedEventArgs e) => Vm?.OpenPrOnGitHub();

	void OnOpenVsCode(object? sender, RoutedEventArgs e) => Vm?.OpenInVsCode();

	void OnEnterCommitScope(object? sender, RoutedEventArgs e) => Vm?.EnterCommitScope();

	void OnPreviousCommit(object? sender, RoutedEventArgs e) => Vm?.StepCommitScope(-1);

	void OnNextCommit(object? sender, RoutedEventArgs e) => Vm?.StepCommitScope(1);

	void OnExitCommitScope(object? sender, RoutedEventArgs e) => Vm?.ExitCommitScope();

	void OnRefreshChecks(object? sender, RoutedEventArgs e) => Vm?.RefreshChecks();

	void OnOpenFixtures(object? sender, RoutedEventArgs e) => Vm?.OpenFixturesInIlspy();

	void OnCommitDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (Vm is { } vm && CommitsList.SelectedItem is CommitLine line)
			vm.OpenCommit(line);
	}

	void OnCheckClick(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && (sender as Button)?.DataContext is CheckLine line)
			vm.OpenCheck(line);
	}

	void OnRecord(object? sender, RoutedEventArgs e) => Vm?.OpenRecord();

	void OnCloseReview(object? sender, RoutedEventArgs e) => App.Workspace?.CloseReview();

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
