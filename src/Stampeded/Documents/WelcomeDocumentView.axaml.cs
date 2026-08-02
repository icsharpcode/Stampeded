using Avalonia.Controls;

using AvaloniaEdit.Search;

using Stampeded.Editor;

namespace Stampeded.Documents;

public partial class WelcomeDocumentView : UserControl
{
	public WelcomeDocumentView()
	{
		InitializeComponent();
		SearchPanel.Install(Editor);
		Editor.SyntaxHighlighting = HighlightingService.GetByExtension(".cs");
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (DataContext is WelcomeDocumentViewModel vm)
			Editor.Text = vm.SampleText;
	}
}
