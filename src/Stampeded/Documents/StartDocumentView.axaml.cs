using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

using Stampeded.Core.GitHub;

namespace Stampeded.Documents;

public partial class StartDocumentView : UserControl
{
	public StartDocumentView()
	{
		InitializeComponent();
	}

	StartDocumentViewModel? Vm => DataContext as StartDocumentViewModel;

	void OnPrRefresh(object? sender, RoutedEventArgs e) => Vm?.PrList.LoadAsync().HandleExceptions();

	void OnRefsRefresh(object? sender, RoutedEventArgs e) => Vm?.ReloadRefs();

	void OnPrOpen(object? sender, RoutedEventArgs e) => OpenSelectedPr();

	void OnPrDoubleTapped(object? sender, TappedEventArgs e) => OpenSelectedPr();

	void OpenSelectedPr()
	{
		if (Vm is { } vm && PrListBox.SelectedItem is PrSummary pr)
			vm.OpenPr(pr);
	}

	void OnBranchOpen(object? sender, RoutedEventArgs e) => OpenSelectedBranch();

	void OnBranchDoubleTapped(object? sender, TappedEventArgs e) => OpenSelectedBranch();

	void OpenSelectedBranch()
	{
		if (Vm is { } vm && BranchList.SelectedItem is BranchRow row)
			vm.OpenBranch(row);
	}

	void OnCopyBranchName(object? sender, RoutedEventArgs e)
	{
		if (Vm is not { } vm || BranchList.SelectedItem is not BranchRow row)
			return;
		string name = row.Info.Name;
		TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(name).HandleExceptions();
		vm.State.Status = $"Copied '{name}' to the clipboard.";
	}

	/// <summary>Avalonia's negated binding is one-way, so a radio pair cannot round-trip
	/// a single bool through it; the group's selection is applied to the view model
	/// here instead.</summary>
	void OnRefKindChanged(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm)
			vm.State.ShowStashes = StashesMode.IsChecked == true;
	}

	void OnFetch(object? sender, RoutedEventArgs e) => Vm?.Fetch();

	void OnPullBranch(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && BranchList.SelectedItem is BranchRow row)
			vm.PullBranchRow(row);
	}

	void OnPushBranch(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && BranchList.SelectedItem is BranchRow row)
			vm.PushBranchRow(row);
	}

	void OnDeleteBranch(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && BranchList.SelectedItem is BranchRow row)
			vm.DeleteBranchRow(row);
	}

	void OnOpenWorktree(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && BranchList.SelectedItem is BranchRow row)
			vm.OpenWorktree(row);
	}

	void OnPrPull(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && PrListBox.SelectedItem is PrSummary pr)
			vm.PullBranch(pr.HeadRefName);
	}

	void OnRebaseBranch(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && BranchList.SelectedItem is BranchRow row)
			vm.RebaseBranch(row);
	}

	void OnRebasePr(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && BranchList.SelectedItem is BranchRow row)
			vm.RebasePr(row);
	}

	void OnCreateBranchFromStash(object? sender, RoutedEventArgs e)
		=> PromptBranchNameAsync().HandleExceptions();

	async Task PromptBranchNameAsync()
	{
		if (BranchList.SelectedItem is BranchRow { IsStash: true } row)
			await PromptBranchNameForAsync(row);
	}

	async Task PromptBranchNameForAsync(BranchRow row)
	{
		if (Vm is not { } vm)
			return;
		if (TopLevel.GetTopLevel(this) is not Window owner)
			return;
		string? name = await new TextPromptWindow("Create branch from stash",
			$"New branch pointing at {row.Info.Name} ({row.Info.Subject}). The stash is kept and nothing is checked out.",
			"Create", "stash/my-work").ShowDialog<string?>(owner);
		if (!string.IsNullOrWhiteSpace(name))
			vm.CreateBranchFromStash(row, name);
	}

	static T? RowOf<T>(object? sender) where T : class => (sender as Control)?.DataContext as T;

	void OnRowPrReview(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<PrSummary>(sender) is { } pr)
			vm.OpenPr(pr);
	}

	void OnRowPrGitHub(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<PrSummary>(sender) is { } pr)
			vm.OpenPrOnGitHub(pr);
	}

	void OnRowBranchReview(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<BranchRow>(sender) is { } row)
			vm.OpenBranch(row);
	}

	void OnRowBranchRebase(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<BranchRow>(sender) is { } row)
			vm.RebaseBranch(row);
	}

	void OnRowBranchPull(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<BranchRow>(sender) is { } row)
			vm.PullBranchRow(row);
	}

	void OnRowBranchPush(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<BranchRow>(sender) is { } row)
			vm.PushBranchRow(row);
	}

	void OnRowBranchDelete(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<BranchRow>(sender) is { } row)
			vm.DeleteBranchRow(row);
	}

	void OnRowBranchWorktree(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<BranchRow>(sender) is { } row)
			vm.OpenWorktree(row);
	}

	void OnRowPrPull(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<PrSummary>(sender) is { } pr)
			vm.PullBranch(pr.HeadRefName);
	}

	void OnRowBranchPrGitHub(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && RowOf<BranchRow>(sender) is { } row)
			vm.OpenBranchPrOnGitHub(row);
	}

	void OnRowStashBranch(object? sender, RoutedEventArgs e)
	{
		if (RowOf<BranchRow>(sender) is { IsStash: true } row)
			PromptBranchNameForAsync(row).HandleExceptions();
	}

	void OnPrOpenOnGitHub(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && PrListBox.SelectedItem is PrSummary pr)
			vm.OpenPrOnGitHub(pr);
	}

	void OnBranchPrOnGitHub(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && BranchList.SelectedItem is BranchRow row)
			vm.OpenBranchPrOnGitHub(row);
	}

	void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (Vm is { } vm && RecentList.SelectedItem is string path)
			vm.OpenRecent(path);
	}

	void OnOpenRepository(object? sender, RoutedEventArgs e) => PickRepositoryAsync().HandleExceptions();

	async Task PickRepositoryAsync()
	{
		var top = TopLevel.GetTopLevel(this);
		if (top is null)
			return;
		var picks = await top.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions {
			Title = "Open git repository",
			AllowMultiple = false,
		});
		if (picks.Count == 1)
			await App.OpenRepositoryAsync(picks[0].Path.LocalPath);
	}

	void OnOpenFromUrl(object? sender, RoutedEventArgs e) => PromptUrlAsync().HandleExceptions();

	async Task PromptUrlAsync()
	{
		if (TopLevel.GetTopLevel(this) is not Window owner)
			return;
		string? url = await new TextPromptWindow("Open from URL",
			"GitHub repository or pull request URL (also accepts owner/repo). A repository not cloned yet is cloned via gh into ~/Projects.",
			"Open", "https://github.com/owner/repo/pull/123").ShowDialog<string?>(owner);
		if (!string.IsNullOrWhiteSpace(url))
			await App.OpenFromUrlAsync(url);
	}
}
