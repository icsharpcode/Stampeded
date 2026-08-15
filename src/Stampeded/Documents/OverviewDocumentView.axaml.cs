using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Stampeded.Documents;

public partial class OverviewDocumentView : UserControl
{
	public OverviewDocumentView()
	{
		InitializeComponent();
		// The description is full of links - the issues it closes, the discussions it came
		// from - and they have to go somewhere when pressed.
		DescriptionView.Engine = Editor.MarkdownLinks.NewEngine();
		// Takes focus itself so 'o' has somewhere to land: the overview is a page of text and
		// buttons, none of which would otherwise hold the keyboard.
		Focusable = true;
	}

	/// <summary>The one overview view. There is a single overview document per window, so
	/// the code that shows it can reach the control without a lookup.</summary>
	public static OverviewDocumentView? Current { get; private set; }

	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		Current = this;
	}

	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		if (Current == this)
			Current = null;
		base.OnDetachedFromVisualTree(e);
	}

	/// <summary>'o' goes back to the file the overview was opened from, the same key that
	/// left it. Skipped while a text box has focus, so it stays typeable.</summary>
	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (!e.Handled && e.Key == Key.O && e.KeyModifiers == KeyModifiers.None
			&& TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox)
		{
			App.Workspace?.ToggleOverviewAsync().HandleExceptions();
			e.Handled = true;
		}
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

	void OnCloseReview(object? sender, RoutedEventArgs e) => App.Workspace?.CloseReviewAsync().HandleExceptions();

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
