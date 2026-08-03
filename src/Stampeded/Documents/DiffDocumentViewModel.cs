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

	int? pendingCaretLine;

	public event Action<int>? CaretRequested;

	public void RequestCaret(int docLine)
	{
		pendingCaretLine = docLine;
		CaretRequested?.Invoke(docLine);
	}

	public int? TakePendingCaretLine()
	{
		int? line = pendingCaretLine;
		pendingCaretLine = null;
		return line;
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
