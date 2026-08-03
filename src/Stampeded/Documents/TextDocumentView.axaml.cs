using Avalonia.Controls;
using Avalonia.Interactivity;

using AvaloniaEdit.Search;

namespace Stampeded.Documents;

public partial class TextDocumentView : UserControl
{
	public TextDocumentView()
	{
		InitializeComponent();
		SearchPanel.Install(Editor);
		DataContextChanged += (_, _) => {
			if (DataContext is TextDocumentViewModel vm)
				Editor.Document = new AvaloniaEdit.Document.TextDocument(vm.Text);
		};
	}

	void OnCtxCopy(object? sender, RoutedEventArgs e) => Editor.Copy();
}
