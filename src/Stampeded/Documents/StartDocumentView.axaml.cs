using Avalonia.Controls;
using Avalonia.Input;
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
			vm.OpenBranch(row.Info);
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
		string? url = await new UrlPromptWindow().ShowDialog<string?>(owner);
		if (!string.IsNullOrWhiteSpace(url))
			await App.OpenFromUrlAsync(url);
	}
}
