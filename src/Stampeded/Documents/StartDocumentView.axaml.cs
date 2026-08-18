using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

	/// <summary>The filter box and its button, for the list, box or button given.</summary>
	(ToggleButton Toggle, TextBox Box)? FilterOf(object? sender)
	{
		if (ReferenceEquals(sender, RecentList) || ReferenceEquals(sender, RecentFilterBox)
			|| ReferenceEquals(sender, RecentFilterToggle))
		{
			return (RecentFilterToggle, RecentFilterBox);
		}
		if (ReferenceEquals(sender, PrListBox) || ReferenceEquals(sender, PrFilterBox)
			|| ReferenceEquals(sender, PrFilterToggle))
		{
			return (PrFilterToggle, PrFilterBox);
		}
		if (ReferenceEquals(sender, BranchList) || ReferenceEquals(sender, BranchFilterBox)
			|| ReferenceEquals(sender, BranchFilterToggle))
		{
			return (BranchFilterToggle, BranchFilterBox);
		}
		return null;
	}

	/// <summary>Opens or closes a column's filter box, which sits over that column's title.
	/// Closing it clears the text: a filter that is still narrowing a list it no longer shows
	/// is a list quietly missing rows.</summary>
	void OnFilterToggled(object? sender, RoutedEventArgs e)
	{
		if (FilterOf(sender) is not ({ } toggle, { } box))
			return;
		if (toggle.IsChecked == true)
		{
			// After the click, not during it: the button takes focus itself as it finishes
			// handling the press, which would take it straight back off the box.
			Avalonia.Threading.Dispatcher.UIThread.Post(() => box.Focus());
		}
		else
		{
			box.Text = "";
		}
	}

	/// <summary>Typing into one of the lists filters it, which is how the filter is found
	/// without knowing the button is there. The character typed starts the filter rather than
	/// being swallowed by the box opening.</summary>
	void OnListTextInput(object? sender, TextInputEventArgs e)
	{
		if (e.Text is not { Length: > 0 } text || char.IsControl(text[0]))
			return;
		if (FilterOf(sender) is not ({ } toggle, { } box))
			return;
		toggle.IsChecked = true;
		box.Text += text;
		Avalonia.Threading.Dispatcher.UIThread.Post(() => {
			box.Focus();
			box.CaretIndex = box.Text?.Length ?? 0;
		});
		e.Handled = true;
	}

	/// <summary>Ctrl+F opens the filter of the list that has focus, for the reader who reaches
	/// for it before typing.</summary>
	void OnListKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.F || e.KeyModifiers != KeyModifiers.Control)
			return;
		if (FilterOf(sender) is not ({ } toggle, { } box))
			return;
		toggle.IsChecked = true;
		Avalonia.Threading.Dispatcher.UIThread.Post(() => box.Focus());
		e.Handled = true;
	}

	/// <summary>Escape closes a filter box, which clears it and puts the whole list back.</summary>
	void OnFilterKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Escape || FilterOf(sender) is not ({ } toggle, _))
			return;
		toggle.IsChecked = false;
		e.Handled = true;
	}

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
