using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Diff;

namespace Stampeded.Documents;

public class DiffDocumentViewModel(FileDiff file, DiffDocumentModel model) : Document
{
	public FileDiff File { get; } = file;
	public DiffDocumentModel Model { get; } = model;
}
