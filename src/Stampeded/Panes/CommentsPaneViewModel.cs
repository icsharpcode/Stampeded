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

	/// <summary>False on the user's own pull request, where GitHub accepts only a plain
	/// comment review.</summary>
	[ObservableProperty]
	bool canGiveVerdict = true;

	/// <summary>False for a local review, which has no pull request to post to.</summary>
	[ObservableProperty]
	bool canComment = true;
}

public sealed record CommentRow(string RelPath, int? Line, bool OldSide, string Body, Guid? DraftId, string Author,
	string? Url = null, bool IsReply = false, string? ThreadId = null, bool IsResolved = false)
{
	/// <summary>Whether this comment's thread can be resolved on GitHub: a posted one, in a
	/// thread GitHub named. A draft has no thread until it is submitted.</summary>
	public bool CanResolve => ThreadId is { Length: > 0 };

	/// <summary>Compact excerpt for the list row; the inline thread box (and the row
	/// tooltip) carry the full text. Long bodies made the pane's layout pass crawl.</summary>
	public string Preview {
		get {
			string flat = Body.ReplaceLineEndings(" ").Trim();
			return flat.Length <= 220 ? flat : flat[..220] + "...";
		}
	}

	public string Header =>
		$"{(DraftId is not null ? IsReply ? "draft reply" : "draft" : Author)}  ·  {RelPath}:{(Line?.ToString() ?? "outdated")}{(OldSide ? " (base)" : "")}";

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
		workspace.Comments.Changed += Rebuild;
		workspace.Comments.TargetRequested += OnTargetRequested;
	}

	void OnTargetRequested()
	{
		var target = workspace.Comments.PendingTarget;
		State.Target = target is null
			? ""
			: $"Comment on {target.RelPath}:{target.Line}{(target.OldSide ? " (base)" : "")}  |  {target.LineText.Trim()}";
	}

	void Rebuild()
	{
		Items.Clear();
		foreach (var draft in workspace.Comments.Drafts)
		{
			Items.Add(new CommentRow(
				draft.Stored.Anchor.Path, draft.CurrentLine, draft.Stored.Anchor.OldSide,
				draft.Stored.Body, draft.Stored.Id, "", null, draft.Stored.InReplyTo is not null));
		}
		foreach (var posted in workspace.Comments.Posted)
		{
			string author = posted.IsResolved ? $"[resolved] {posted.Author}" : posted.Author;
			Items.Add(new CommentRow(posted.RelPath, posted.Line, posted.OldSide, posted.Body, null, author,
				posted.Url, IsReply: false, posted.ThreadId, posted.IsResolved));
		}
		State.CanComment = workspace.Comments.CanComment;
		// A reply posts into its thread by id, so a lost line does not put it at risk and it
		// is not what "outdated" warns about.
		int outdated = workspace.Comments.Drafts.Count(d => d.CurrentLine is null && d.Stored.InReplyTo is null);
		State.Status = State.CanComment
			? $"{workspace.Comments.Drafts.Count} draft(s){(outdated > 0 ? $" ({outdated} outdated)" : "")}, {workspace.Comments.Posted.Count} posted."
			: "Local review: comments need a pull request to post to.";
		RefreshVerdictAvailabilityAsync().HandleExceptions();

		async Task RefreshVerdictAvailabilityAsync()
			=> State.CanGiveVerdict = workspace.Comments.CanComment && !await workspace.Comments.IsOwnPullRequestAsync();
	}

	public void AddDraft()
	{
		string body = State.DraftBody.Trim();
		if (body.Length == 0)
			return;
		CommitAsync(body).HandleExceptions();

		async Task CommitAsync(string text)
		{
			await workspace.Comments.CommitDraftAsync(text);
			State.DraftBody = "";
			State.Target = "";
		}
	}

	public void Refresh()
	{
		RefreshAsync().HandleExceptions();

		async Task RefreshAsync()
		{
			State.Status = "Refreshing posted comments...";
			try
			{
				await workspace.Comments.RefreshPostedAsync();
			}
			catch (ToolFailedException ex)
			{
				// Rebuild would otherwise overwrite this with a count that did not change.
				State.Status = $"Refresh failed: {ex.Message}";
			}
		}
	}

	public void RemoveSelected(CommentRow row)
	{
		if (row.DraftId is { } id)
			workspace.Comments.RemoveDraft(id);
	}

	/// <summary>Resolves or reopens one thread on GitHub. What the pane shows follows from
	/// what GitHub says afterwards, not from what was asked for: the request can be refused -
	/// a repository where resolving is not the reviewer's to do - and a row that reads
	/// resolved when it is not is worse than one that did not change.</summary>
	public void SetResolved(CommentRow row, bool resolved)
	{
		if (row.ThreadId is { Length: > 0 } threadId)
			workspace.Comments.SetThreadResolvedAsync(threadId, resolved).HandleExceptions();
	}

	/// <summary>Whether anything here is still open, which is what "all" would act on.</summary>
	public bool HasUnresolvedThreads
		=> workspace.Comments.Posted.Any(p => !p.IsResolved && p.ThreadId is { Length: > 0 });

	/// <summary>Resolves every thread that is not resolved yet - the end of a pass where each
	/// remark has been dealt with, which is otherwise a right-click per comment.</summary>
	public void ResolveAll()
	{
		ResolveAllAsync().HandleExceptions();

		async Task ResolveAllAsync()
		{
			var threads = workspace.Comments.Posted
				.Where(p => !p.IsResolved && p.ThreadId is { Length: > 0 })
				.Select(p => p.ThreadId!)
				.Distinct(StringComparer.Ordinal)
				.ToList();
			if (threads.Count == 0)
			{
				State.Status = "No unresolved threads here.";
				return;
			}
			foreach (string threadId in threads)
				await workspace.Comments.SetThreadResolvedAsync(threadId, resolved: true);
			State.Status = $"Marked {threads.Count} thread(s) resolved.";
		}
	}

	public void OpenOnGitHub(CommentRow row)
	{
		if (row.Url is { Length: > 0 } url)
			workspace.OpenUrlAsync(url).HandleExceptions();
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
		SubmitAsync().HandleExceptions();

		async Task SubmitAsync()
		{
			string body = State.ReviewBody.Trim();
			State.Status = await workspace.Comments.SubmitCheckedAsync(eventType, body);
			if (State.Status.StartsWith("Review submitted", StringComparison.Ordinal))
				State.ReviewBody = "";
		}
	}
}
