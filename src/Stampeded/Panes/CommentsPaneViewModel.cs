using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Panes;

public sealed partial class CommentsState : ObservableObject
{
	[ObservableProperty]
	string status = "Press 'c' on a diff line (or right-click > Comment Here) to draft a comment.";

	[ObservableProperty]
	string target = "";

	[ObservableProperty]
	string draftBody = "";

	[ObservableProperty]
	string reviewBody = "";
}

public sealed record CommentRow(string RelPath, int? Line, bool OldSide, string Body, Guid? DraftId, string Author)
{
	public string Header =>
		$"{(DraftId is not null ? "draft" : Author)}  ·  {RelPath}:{(Line?.ToString() ?? "outdated")}{(OldSide ? " (base)" : "")}";

	public bool IsDraft => DraftId is not null;
}

/// <summary>
/// Draft and posted review comments, plus review submission (approve / request changes /
/// comment). Drafts anchored by content survive force-pushes; unresolvable ones show as
/// outdated and stay local when submitting.
/// </summary>
public class CommentsPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;

	public ObservableCollection<CommentRow> Items { get; } = [];
	public CommentsState State { get; } = new();

	public CommentsPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.CommentsChanged += Rebuild;
		workspace.CommentTargetRequested += OnTargetRequested;
	}

	void OnTargetRequested()
	{
		var target = workspace.PendingCommentTarget;
		State.Target = target is null
			? ""
			: $"Comment on {target.RelPath}:{target.Line}{(target.OldSide ? " (base)" : "")}  |  {target.LineText.Trim()}";
	}

	void Rebuild()
	{
		Items.Clear();
		foreach (var draft in workspace.Drafts)
		{
			Items.Add(new CommentRow(
				draft.Stored.Anchor.Path, draft.CurrentLine, draft.Stored.Anchor.OldSide,
				draft.Stored.Body, draft.Stored.Id, ""));
		}
		foreach (var posted in workspace.PostedComments)
		{
			string author = posted.IsResolved ? $"[resolved] {posted.Author}" : posted.Author;
			Items.Add(new CommentRow(posted.RelPath, posted.Line, posted.OldSide, posted.Body, null, author));
		}
		int outdated = workspace.Drafts.Count(d => d.CurrentLine is null);
		State.Status = $"{workspace.Drafts.Count} draft(s){(outdated > 0 ? $" ({outdated} outdated)" : "")}, {workspace.PostedComments.Count} posted.";
	}

	public void AddDraft()
	{
		string body = State.DraftBody.Trim();
		if (body.Length == 0)
			return;
		CommitAsync(body).HandleExceptions();

		async Task CommitAsync(string text)
		{
			await workspace.CommitDraftAsync(text);
			State.DraftBody = "";
			State.Target = "";
		}
	}

	public void RemoveSelected(CommentRow row)
	{
		if (row.DraftId is { } id)
			workspace.RemoveDraft(id);
	}

	public void Open(CommentRow row)
	{
		// Line-less (outdated, unapproximable) comments still open their file: the
		// thread box is pinned above its first line.
		workspace.NavigateToFileLineAsync(row.RelPath, row.Line ?? 1, row.OldSide && row.Line is not null, record: true)
			.HandleExceptions();
	}

	public void Submit(string eventType)
	{
		if (eventType == "APPROVE" && workspace.ApprovalGate?.Invoke() is { Ok: false } gate)
		{
			State.Status = $"Approval blocked by the review guide - incomplete: {gate.Detail}  (override in the Guide pane)";
			return;
		}
		SubmitAsync(eventType).HandleExceptions();

		async Task SubmitAsync(string type)
		{
			try
			{
				var (submitted, skipped) = await workspace.SubmitReviewAsync(type, State.ReviewBody.Trim());
				State.ReviewBody = "";
				State.Status = $"Review submitted ({type}): {submitted} comment(s) posted{(skipped > 0 ? $", {skipped} kept local (outdated/off-diff)" : "")}.";
			}
			catch (ToolFailedException ex)
			{
				State.Status = ex.Message;
			}
		}
	}
}
