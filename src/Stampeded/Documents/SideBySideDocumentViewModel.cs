using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Diff;

namespace Stampeded.Documents;

public class SideBySideDocumentViewModel(FileDiff file, SideBySideModel pair) : Document, IDiffDocument
{
	public FileDiff File { get; } = file;
	public SideBySideModel Pair { get; } = pair;

	/// <inheritdoc cref="DiffDocumentViewModel.TabTooltip" />
	public string TabTooltip => File.Path;

	(int Line, bool OldSide)? pendingCaret;

	public event Action<int, bool>? CaretRequested;

	/// <inheritdoc />
	public void RequestCaret(int blobLine, bool oldSide = false)
	{
		pendingCaret = (blobLine, oldSide);
		CaretRequested?.Invoke(blobLine, oldSide);
	}

	/// <summary>The caret asked for before the view existed to hear it, taken once.</summary>
	public (int Line, bool OldSide)? TakePendingCaret()
	{
		var pending = pendingCaret;
		pendingCaret = null;
		return pending;
	}
}
