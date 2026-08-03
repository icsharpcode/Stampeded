using Dock.Model.Mvvm.Controls;

namespace Stampeded.Documents;

/// <summary>
/// A plain read-only text tab (PR overview, CI logs, commit patches, load logs) - prose,
/// not code, so it renders without any diff chrome and with word wrap.
/// </summary>
public class TextDocumentViewModel(string text) : Document
{
	public string Text { get; } = text;
}
