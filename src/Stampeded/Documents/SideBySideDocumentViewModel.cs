using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Diff;

namespace Stampeded.Documents;

public class SideBySideDocumentViewModel(FileDiff file, SideBySideModel pair) : Document
{
	public FileDiff File { get; } = file;
	public SideBySideModel Pair { get; } = pair;
}
