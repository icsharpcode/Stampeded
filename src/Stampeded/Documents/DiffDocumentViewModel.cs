using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Diff;

namespace Stampeded.Documents;

public class DiffDocumentViewModel(FileDiff file, DiffDocumentModel model) : Document
{
	public FileDiff File { get; } = file;

	/// <summary>The diff as built from the blobs, without synthetic thread lines; the
	/// base every comment-thread re-splice starts from.</summary>
	public DiffDocumentModel PristineModel { get; } = model;

	public DiffDocumentModel Model { get; private set; } = model;

	/// <summary>Swaps the displayed model (e.g. after inserting comment-thread lines);
	/// the view re-applies text, tags and foldings.</summary>
	public void ReplaceModel(DiffDocumentModel replacement) => Model = replacement;

	/// <summary>True for plain source views of unchanged files (identity diff model).</summary>
	public bool IsSourceView { get; private init; }

	/// <summary>True for a historical commit diff: line numbers reference that commit's
	/// blobs, not the review head/base, so semantics, comments and coverage stay off.</summary>
	public bool Historical { get; init; }

	/// <summary>The commit shown when <see cref="Historical"/> (blame runs against it).</summary>
	public string? HistoricalSha { get; init; }

	(int Line, bool OldSide)? pendingCaret;

	public event Action<int, bool>? CaretRequested;

	/// <summary>
	/// Asks for the caret at a line of the file, on the given side. In file coordinates, not
	/// the document's: the document gains and loses lines as comment threads are spliced into
	/// it, and a request made before that happens would land wherever those lines pushed it.
	/// </summary>
	public void RequestCaret(int blobLine, bool oldSide = false)
	{
		pendingCaret = (blobLine, oldSide);
		CaretRequested?.Invoke(blobLine, oldSide);
	}

	public (int Line, bool OldSide)? TakePendingCaret()
	{
		var pending = pendingCaret;
		pendingCaret = null;
		return pending;
	}

	/// <summary>A read-only source view over an unchanged worktree file, sharing the whole
	/// diff-editor component with an identity line map and no hunks.</summary>
	public static DiffDocumentViewModel ForSource(string relPath, string text)
	{
		var stub = new FileDiff(relPath, relPath, FileChangeKind.Modified, false, []);
		return new DiffDocumentViewModel(stub, DiffDocumentBuilder.Build(text, text)) {
			Title = Path.GetFileName(relPath),
			IsSourceView = true,
		};
	}
}
