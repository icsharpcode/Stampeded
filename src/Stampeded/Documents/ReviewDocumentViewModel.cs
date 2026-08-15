using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Documents;

/// <summary>One comment with the lines it was written about.</summary>
public sealed record ReviewCommentRow(
	string RelPath, int? Line, bool OldSide, string Author, string Body, string Context, bool IsDraft)
{
	public string Header =>
		$"{RelPath}:{(Line?.ToString() ?? "outdated")}{(OldSide ? " (base)" : "")}";

	public string Who => IsDraft ? "draft (not posted yet)" : Author;
}

public sealed partial class ReviewDocumentState : ObservableObject
{
	[ObservableProperty]
	string status = "";

	[ObservableProperty]
	string summary = "";

	[ObservableProperty]
	bool canGiveVerdict;

	[ObservableProperty]
	bool canComment;

	[ObservableProperty]
	bool isEmpty = true;
}

/// <summary>
/// Every comment of this review in one place, each under the lines it is about, with the
/// verdict at the bottom - the page a reader reaches for when deciding, rather than a list of
/// one-line previews in a pane that shows the code only after a jump.
/// </summary>
public sealed class ReviewDocumentViewModel : Document
{
	readonly ReviewWorkspace workspace;

	/// <summary>Blob text per revision and path: several comments usually land in the same
	/// file, and each one only needs a handful of lines out of it.</summary>
	readonly Dictionary<(string Rev, string Path), string[]> blobs = new();

	public ObservableCollection<ReviewCommentRow> Comments { get; } = [];
	public ReviewDocumentState State { get; } = new();

	/// <summary>Lines of code shown on each side of the commented line.</summary>
	const int ContextLines = 3;

	public ReviewDocumentViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.CommentsChanged += Rebuild;
		Rebuild();
	}

	void Rebuild() => RebuildAsync().HandleExceptions();

	async Task RebuildAsync()
	{
		Comments.Clear();
		foreach (var draft in workspace.Drafts)
		{
			Comments.Add(new ReviewCommentRow(
				draft.Stored.Anchor.Path, draft.CurrentLine, draft.Stored.Anchor.OldSide, "",
				draft.Stored.Body,
				await ContextAsync(draft.Stored.Anchor.Path, draft.CurrentLine, draft.Stored.Anchor.OldSide),
				IsDraft: true));
		}
		foreach (var posted in workspace.PostedComments)
		{
			Comments.Add(new ReviewCommentRow(
				posted.RelPath, posted.Line, posted.OldSide,
				posted.IsResolved ? $"{posted.Author} (resolved)" : posted.Author,
				posted.Body,
				await ContextAsync(posted.RelPath, posted.Line, posted.OldSide),
				IsDraft: false));
		}
		State.IsEmpty = Comments.Count == 0;
		State.CanComment = workspace.CanComment;
		int drafts = workspace.Drafts.Count;
		State.Status = workspace.CanComment
			? $"{drafts} draft(s) will be posted with this review; {workspace.PostedComments.Count} comment(s) already on the pull request."
			: "Local review: comments need a pull request to post to.";
		State.CanGiveVerdict = workspace.CanComment && !await workspace.IsOwnPullRequestAsync();
	}

	/// <summary>
	/// The commented line with a few above and below it, each numbered, the commented one
	/// marked. Empty when there is no line to show it at - an outdated draft has lost the
	/// place it was written about, which is the honest thing to say about it.
	/// </summary>
	async Task<string> ContextAsync(string path, int? line, bool oldSide)
	{
		if (line is not { } target)
			return "";
		string? rev = oldSide ? workspace.BaseSha : workspace.HeadSha;
		if (rev is null)
			return "";
		if (!blobs.TryGetValue((rev, path), out var lines))
		{
			try
			{
				lines = (await workspace.Git.ShowFileAsync(rev, path)).ReplaceLineEndings("\n").Split('\n');
			}
			catch (ToolFailedException)
			{
				// Added on the other side, or renamed: no blob to quote from at this revision.
				lines = [];
			}
			blobs[(rev, path)] = lines;
		}
		if (lines.Length == 0)
			return "";
		int first = Math.Max(1, target - ContextLines);
		int last = Math.Min(lines.Length, target + ContextLines);
		var text = new System.Text.StringBuilder();
		for (int i = first; i <= last; i++)
			text.AppendLine($"{(i == target ? ">" : " ")} {i,5}  {lines[i - 1]}");
		return text.ToString().TrimEnd('\n');
	}

	public void Open(ReviewCommentRow row)
		=> workspace.NavigateToFileLineAsync(row.RelPath, row.Line ?? 1, row.OldSide && row.Line is not null, record: true)
			.HandleExceptions();

	public void Submit(string eventType)
	{
		SubmitAsync().HandleExceptions();

		async Task SubmitAsync()
		{
			State.Status = await workspace.SubmitReviewCheckedAsync(eventType, State.Summary.Trim());
			if (State.Status.StartsWith("Review submitted", StringComparison.Ordinal))
				State.Summary = "";
		}
	}

	public void Refresh() => RefreshAsync().HandleExceptions();

	async Task RefreshAsync()
	{
		State.Status = "Refreshing posted comments...";
		try
		{
			await workspace.RefreshPostedCommentsAsync();
		}
		catch (ToolFailedException ex)
		{
			// Rebuild would otherwise overwrite this with a count that did not change.
			State.Status = $"Refresh failed: {ex.Message}";
		}
	}
}
