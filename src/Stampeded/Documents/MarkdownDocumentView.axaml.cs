using Avalonia.Controls;

namespace Stampeded.Documents;

public partial class MarkdownDocumentView : UserControl
{
	public MarkdownDocumentView()
	{
		InitializeComponent();
		// A document under review is full of links - to its own sections, to issues, to the
		// sites it cites - and without an engine that runs them a link renders as a link and
		// does nothing when pressed.
		Rendered.Engine = Editor.MarkdownLinks.NewEngine();
		// A preview is read to be quoted from, in a comment about the paragraph it shows. The
		// renderer paints a selection but carries nothing that copies one, and the blocks it
		// draws never take focus, so the gesture is handled by the page.
		Controls.MarkdownSelection.Enable(this);
		// The renderer draws the markers of an emphasis reaching around a link or a code span
		// instead of the emphasis itself; they are paired again from the pieces it left, once
		// it has drawn. Posted, because the rendered blocks exist only after the layout pass.
		Rendered.PropertyChanged += (_, e) => {
			if (e.Property == global::Markdown.Avalonia.MarkdownScrollViewer.MarkdownProperty)
			{
				Avalonia.Threading.Dispatcher.UIThread.Post(
					() => Controls.MarkdownEmphasis.Repair(Rendered),
					Avalonia.Threading.DispatcherPriority.Loaded);
			}
		};
	}
}
