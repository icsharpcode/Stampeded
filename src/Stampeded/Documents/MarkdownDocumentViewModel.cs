using Dock.Model.Mvvm.Controls;

namespace Stampeded.Documents;

/// <summary>
/// A markdown file as it renders, in a tab of its own beside the diff of its source. A change
/// to a README or a document is read for what it will look like as much as for what it says,
/// and the diff can only show the source.
/// </summary>
public class MarkdownDocumentViewModel(string text) : Document
{
	public string Text { get; } = text;

	/// <summary>What the tab strip says about this tab on hover. The title is a file name and
	/// several documents can share one; the path is what tells them apart.</summary>
	public string TabTooltip { get; init; } = "";
}
