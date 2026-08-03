using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using Stampeded.Core.Git;
using Stampeded.Core.GitHub;

namespace Stampeded.Documents;

public partial class WizardView : UserControl
{
	public WizardView()
	{
		InitializeComponent();
	}

	WizardViewModel? Vm => DataContext as WizardViewModel;

	void OnStepClick(object? sender, RoutedEventArgs e)
	{
		if (Vm is { } vm && (sender as Button)?.DataContext is WizardStep step)
			vm.SelectStepCommand(step);
	}

	void OnBack(object? sender, RoutedEventArgs e) => Vm?.PreviousStep();

	void OnNext(object? sender, RoutedEventArgs e) => Vm?.NextStep();

	void OnSkip(object? sender, RoutedEventArgs e) => Vm?.SkipStep();

	void OnContinueFromPrepare(object? sender, RoutedEventArgs e) => Vm?.ContinueFromPrepare();

	void OnBounce(object? sender, RoutedEventArgs e) => Vm?.Bounce();

	void OnRecord(object? sender, RoutedEventArgs e) => Vm?.OpenRecord();

	void OnPrRefresh(object? sender, RoutedEventArgs e) => Vm?.PrList.LoadAsync().HandleExceptions();

	void OnPrOpenGuided(object? sender, RoutedEventArgs e) => OpenSelectedPr(guided: true);

	void OnPrDoubleTapped(object? sender, TappedEventArgs e) => OpenSelectedPr(guided: true);

	void OnPrOpenPlain(object? sender, RoutedEventArgs e) => OpenSelectedPr(guided: false);

	void OpenSelectedPr(bool guided)
	{
		if (Vm is { } vm && PrListBox.SelectedItem is PrSummary pr)
			vm.OpenPr(pr, guided);
	}

	void OnBranchOpenGuided(object? sender, RoutedEventArgs e) => OpenSelectedBranch(guided: true);

	void OnBranchDoubleTapped(object? sender, TappedEventArgs e) => OpenSelectedBranch(guided: true);

	void OnBranchOpenPlain(object? sender, RoutedEventArgs e) => OpenSelectedBranch(guided: false);

	void OpenSelectedBranch(bool guided)
	{
		if (Vm is { } vm && BranchList.SelectedItem is BranchInfo branch)
			vm.OpenBranch(branch, guided);
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

	void OnSweepDoubleTapped(object? sender, TappedEventArgs e)
	{
		if (Vm is { } vm && SweepList.SelectedItem is ReviewWorkspace.SweepItem item)
			vm.OpenSweepItem(item);
	}
}
