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
}
