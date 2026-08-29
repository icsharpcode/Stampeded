using Avalonia.Controls;

using Stampeded.Documents;

namespace Stampeded.Panes;

public partial class ExplorerPaneView : UserControl
{
	public ExplorerPaneView()
	{
		InitializeComponent();
	}

	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		DiffDocumentView.ActiveViewChanged += OnActiveDocumentChanged;
	}

	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		DiffDocumentView.ActiveViewChanged -= OnActiveDocumentChanged;
		base.OnDetachedFromVisualTree(e);
	}

	void OnActiveDocumentChanged()
	{
		if (DiffDocumentView.ActiveView?.ViewModel?.File.Path is not { Length: > 0 } path)
			return;
		FilesSection.RevealFile(path);
		BrowserSection.RevealAsync(path).HandleExceptions();
	}

	ExplorerPaneViewModel? Vm => DataContext as ExplorerPaneViewModel;

	void OnEnterCommitScope(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.EnterCommitScope();

	void OnSinceLastPass(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.EnterSinceLastPass();

	void OnPreviousCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.StepCommit(-1);

	void OnNextCommit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.StepCommit(1);

	void OnExitCommitScope(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.ExitCommitScope();

	void OnOpenVsCode(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.OpenInVsCode();

	void OnOpenOnGitHub(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.OpenPrOnGitHub();

	void OnOpenCommitOnGitHub(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.OpenCommitOnGitHub();

	void OnOpenReview(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.OpenReview();

	void OnCloseReview(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.CloseReview();

	void OnReloadReview(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Vm?.ReloadReview();
}
