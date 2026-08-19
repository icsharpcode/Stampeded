using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;
using Stampeded.Core.Review;

namespace Stampeded;

/// <summary>A comment written in this pass, and the line it currently hangs on -
/// <paramref name="CurrentLine"/> is null once the code it was written about is gone.</summary>
public sealed record DraftComment(StoredComment Stored, int? CurrentLine, bool IsApproximate = false);

/// <summary>Where a comment is being written. <paramref name="InReplyTo"/> names the posted
/// comment this one answers, when the reader is replying rather than starting a thread.</summary>
public sealed record CommentTarget(string RelPath, bool OldSide, int Line, string LineText, long? InReplyTo = null);

/// <summary>A comment already on the pull request, placed in the code as it stands now.</summary>
public sealed record PostedCommentView(string RelPath, int? Line, bool OldSide, string Body, string Author,
	bool IsApproximate = false, string? ThreadId = null, bool IsResolved = false, string? Url = null,
	long CommentId = 0);

/// <summary>
/// The comments of one review: the drafts written in this pass, the ones already on the pull
/// request, where each of them sits in the code as it stands, and what leaves for GitHub when
/// the pass is submitted.
///
/// It is given the review rather than a copy of its parts. Every question here is about that
/// review - which pull request, which two commits, which lines the change touches - and a
/// comment written against a review that has since been reloaded is a comment about nothing.
/// </summary>
public sealed class ReviewComments(ReviewWorkspace workspace)
{
	public IReadOnlyList<DraftComment> Drafts { get; private set; } = [];
	public IReadOnlyList<PostedCommentView> Posted { get; private set; } = [];

	/// <summary>True once the posted-comment fetch for the current review finished
	/// (successfully or not) - distinguishes "none" from "still loading".</summary>
	public bool Loaded { get; private set; }

	public CommentTarget? PendingTarget { get; private set; }

	public event Action? Changed;
	public event Action? TargetRequested;

	/// <summary>Set by the dock factory so 'comment here' can surface the Comments pane.</summary>
	public Dock.Model.Core.IDockable? Pane { get; set; }

	/// <summary>Review comments live on a pull request. A local-branch or uncommitted-work
	/// review has nowhere to post them, so drafting one would only ever produce a draft that
	/// can never leave the machine.</summary>
	public bool CanComment => workspace.CurrentPr is not null;

	/// <summary>Forgets everything of the review being closed.</summary>
	public void Clear()
	{
		Drafts = [];
		Posted = [];
		Loaded = false;
		PendingTarget = null;
		Changed?.Invoke();
	}

	/// <summary>Asks for the comments to be rendered again although none of them changed: what
	/// they are rendered with did. A "#123" in a comment becomes a link only once the repository
	/// it points at is known, and that answer arrives after the review is already on screen.</summary>
	public void Rerender() => Changed?.Invoke();

	/// <summary>A review with no pull request behind it: there is nothing posted to fetch,
	/// which is an answer and not a fetch still outstanding.</summary>
	public void NoneToLoad()
	{
		Posted = [];
		Loaded = true;
		Changed?.Invoke();
	}

	public void BeginComment(CommentTarget target, bool activatePane = true)
	{
		if (!CanComment)
		{
			workspace.PostStatus("Comments need a pull request; this is a local review.");
			return;
		}
		if (target.OldSide && workspace.Scopes.InSinceLastPass)
		{
			// The left side here is the reader's last pass replayed onto the current base,
			// not the pull request's base. GitHub reads a LEFT line against the latter, so
			// this comment would be posted against a line nobody wrote.
			workspace.PostStatus("The left side of this scope is your last pass, not the pull request's base, so a "
				+ "comment there has no line to land on. Press 'Whole change' to comment on removed code.");
			return;
		}
		PendingTarget = target;
		if (activatePane && Pane is not null && workspace.Factory is not null)
			workspace.Factory.SetActiveDockable(Pane);
		TargetRequested?.Invoke();
	}

	public async Task CommitDraftAsync(string body)
	{
		if (PendingTarget is not { } target || body.Length == 0)
			return;
		string rev = target.OldSide ? workspace.BaseSha! : workspace.HeadSha!;
		var lines = SplitBlobLines(await workspace.Git.ShowFileAsync(rev, target.RelPath));
		if (target.Line < 1 || target.Line > lines.Length)
			return;
		var anchor = CommentAnchor.Create(target.RelPath, target.OldSide, target.Line, lines);
		workspace.Store.AddDraft(new StoredComment(Guid.NewGuid(), anchor, body, DateTimeOffset.Now, target.InReplyTo));
		PendingTarget = null;
		RebuildDrafts();
	}

	public void UpdateDraft(Guid id, string body)
	{
		if (body.Trim().Length == 0)
			return;
		workspace.Store.UpdateDraft(id, body);
		RebuildDrafts();
	}

	public void RemoveDraft(Guid id)
	{
		workspace.Store.RemoveDraft(id);
		RebuildDrafts();
	}

	void RebuildDrafts()
	{
		Drafts = [.. workspace.Store.Drafts.Select(d => new DraftComment(d, d.Anchor.Line))];
		Changed?.Invoke();
	}

	static string[] SplitBlobLines(string text)
	{
		if (text.Length == 0)
			return [];
		text = text.ReplaceLineEndings("\n");
		if (text.EndsWith('\n'))
			text = text[..^1];
		return text.Split('\n');
	}

	/// <summary>Re-attaches stored drafts against the current base/head blobs (drafts kept
	/// across force-pushes find their new lines by content; unresolvable ones show as
	/// outdated with CurrentLine null).</summary>
	public async Task ReattachDraftsAsync(CancellationToken ct)
	{
		var reattached = new List<DraftComment>();
		foreach (var stored in workspace.Store.Drafts)
		{
			int? line = null;
			bool approximate = false;
			try
			{
				string rev = stored.Anchor.OldSide ? workspace.BaseSha! : workspace.HeadSha!;
				var lines = SplitBlobLines(await workspace.Git.ShowFileAsync(rev, stored.Anchor.Path, ct));
				line = stored.Anchor.Reattach(lines);
				if (line is null)
				{
					line = stored.Anchor.Approximate(lines);
					approximate = true;
				}
			}
			catch (ToolFailedException)
			{
				// File gone at that revision: outdated with no location at all.
			}
			reattached.Add(new DraftComment(stored, line, approximate));
		}
		Drafts = reattached;
		Changed?.Invoke();
	}

	/// <summary>
	/// Re-reads the posted comments from GitHub. They are fetched when the review opens and
	/// after submitting, so anything said meanwhile - a reply, a resolved thread, a review
	/// from someone else - is invisible until asked for.
	/// </summary>
	public async Task RefreshPostedAsync()
	{
		if (workspace.CurrentPr is not { } pr)
		{
			workspace.PostStatus("No pull request: a local review has no posted comments to fetch.");
			return;
		}
		using var busy = workspace.Busy.Begin("Refreshing comments");
		await LoadPostedAsync(pr.Number, CancellationToken.None);
	}

	public async Task LoadPostedAsync(int number, CancellationToken ct)
	{
		Loaded = false;
		try
		{
			// Offline the snapshot is the answer; asking would only fail slowly. A review
			// opened online keeps what it read, for the next time it cannot be.
			var raw = workspace.Offline
				? workspace.SnapshotComments ?? []
				: await workspace.GitHub.GetReviewCommentsAsync(number, ct);
			if (!workspace.Offline)
				workspace.KeepComments(raw);
			Dictionary<long, (string ThreadId, bool Resolved)> resolutionByComment = [];
			try
			{
				foreach (var thread in await workspace.GitHub.GetThreadResolutionsAsync(number, ct))
				{
					foreach (long id in thread.CommentIds)
						resolutionByComment[id] = (thread.ThreadId, thread.IsResolved);
				}
			}
			catch (ToolFailedException)
			{
				// Resolution state is an enrichment; comments still render without it.
			}
			var views = new List<PostedCommentView>();
			foreach (var comment in raw)
			{
				bool oldSide = comment.Side == "LEFT";
				var (line, approximate) = comment.Line is { } stated
					? ((int?)stated, false)
					: await LocateAsync(comment, oldSide, ct);
				var resolution = resolutionByComment.GetValueOrDefault(comment.Id);
				views.Add(new PostedCommentView(
					comment.Path, line, oldSide, comment.Body, comment.User?.Login ?? "?",
					approximate, resolution.ThreadId, resolution.Resolved, comment.HtmlUrl, comment.Id));
			}
			// Resolved threads are answered business: they stay, because what was said about a
			// file is worth finding again, but after everything still open - and here rather
			// than in each list that shows them, so the pane and the review page agree. The
			// sort is stable, so within each group GitHub's own order is untouched.
			Posted = [.. views.OrderBy(v => v.IsResolved ? 1 : 0)];
		}
		catch (ToolFailedException)
		{
			Posted = [];
		}
		Loaded = true;
		Changed?.Invoke();
	}

	/// <summary>
	/// Where a posted comment belongs in the current blob, once GitHub has stopped saying: the
	/// line its own excerpt was written against, found by content, or the best approximation the
	/// surviving context allows. Both answers come out of one read of the blob - they are the
	/// same anchor asked two ways, and asking separately cost a `git show` each.
	/// </summary>
	async Task<(int? Line, bool Approximate)> LocateAsync(PostedComment comment, bool oldSide, CancellationToken ct)
	{
		if (comment.DiffHunk is not { } hunk || comment.OriginalLine is not { } originalLine
			|| (oldSide ? workspace.BaseSha : workspace.HeadSha) is not { } rev
			|| CommentAnchor.FromDiffHunk(comment.Path, oldSide, originalLine, hunk) is not { } anchor)
		{
			return (null, false);
		}
		try
		{
			var blobLines = SplitBlobLines(await workspace.Git.ShowFileAsync(rev, comment.Path, ct));
			return anchor.Reattach(blobLines) is { } exact
				? (exact, false)
				: (anchor.Approximate(blobLines), true);
		}
		catch (ToolFailedException)
		{
			// The file is not in that revision at all: the comment keeps no location.
			return (null, false);
		}
	}

	/// <summary>Sets a review thread's resolution on GitHub, then refreshes the comments.</summary>
	public async Task SetThreadResolvedAsync(string threadId, bool resolved)
	{
		if (workspace.CurrentPr is not { } pr)
			return;
		try
		{
			await workspace.GitHub.SetThreadResolvedAsync(threadId, resolved);
			await LoadPostedAsync(pr.Number, CancellationToken.None);
		}
		catch (ToolFailedException ex)
		{
			workspace.PostStatus($"Thread resolution failed: {ex.Message}");
		}
	}

	/// <summary>Whether the open review is of the user's own pull request. GitHub rejects
	/// APPROVE and REQUEST_CHANGES on those, so only a plain comment review can be
	/// submitted. False when nothing is open, or when gh cannot say who it is - the
	/// submission itself is the real gate, this only keeps the UI from offering what would
	/// certainly fail.</summary>
	public async Task<bool> IsOwnPullRequestAsync()
	{
		if (workspace.CurrentPr?.Author?.Login is not { Length: > 0 } author)
			return false;
		try
		{
			return string.Equals(author, await workspace.GitHub.GetViewerLoginAsync(), StringComparison.OrdinalIgnoreCase);
		}
		catch (ToolFailedException)
		{
			return false;
		}
	}

	/// <summary>
	/// Submits a review after the checks that can refuse one, and reports what happened in a
	/// line meant for a reader. Both places that submit - the Comments pane and the review view
	/// - go through this, so a verdict cannot be refused in one and slip through the other.
	/// </summary>
	public async Task<string> SubmitCheckedAsync(string eventType, string body)
	{
		if (workspace.Offline)
		{
			return $"Offline: this review was opened from a snapshot taken {workspace.OfflineSince:g}, and a "
				+ $"verdict has to go to GitHub. Reload (F5) when there is a connection; your "
				+ $"{Drafts.Count} draft(s) are kept.";
		}
		// A line comment names a path and a line of the pull request's own head. Read against a
		// branch that has moved past it, the lines on screen are lines GitHub does not have, and
		// a comment posted from here would land on whatever text now sits at that number - or be
		// refused for being outside the diff. A reply names a thread instead and is unaffected.
		if (workspace.LocalHead)
		{
			int placed = Drafts.Count(d => d.Stored.InReplyTo is null);
			if (eventType != "COMMENT" || placed > 0)
			{
				return $"This review is reading the local branch, which is ahead of what #{workspace.CurrentPr?.Number} "
					+ $"shows ({workspace.PrHeadSha?[..9]}). "
					+ (placed > 0
						? $"{placed} draft(s) sit on lines GitHub does not have; push the branch, then submit. "
						: "A verdict is given on the pushed head; push the branch, then submit. ")
					+ "Replies to existing threads can be submitted as a comment review from here.";
			}
		}
		// Drafts are matched against the files in scope, so submitting from one would keep every
		// draft written elsewhere local and report it as outdated - which is not what happened
		// to it. This used to be answered inside the submit, with a status line and a return
		// value that said a review had been submitted: the verdict did nothing and said it had
		// worked.
		if (workspace.Scopes.InScope)
		{
			return "A review covers the whole change, and this is a part of it: press 'Whole change' "
				+ $"first, then approve or decline. Your {Drafts.Count} draft(s) are kept.";
		}
		if (eventType == "APPROVE" && workspace.ApprovalGate?.Invoke() is { Ok: false } gate)
			return $"Approval blocked by the review guide - incomplete: {gate.Detail}  (override in the Guide pane)";
		// The buttons are disabled for these on your own pull request, but the check that
		// disables them is asynchronous, so a submission can still get here first.
		if (eventType is "APPROVE" or "REQUEST_CHANGES" && await IsOwnPullRequestAsync())
		{
			return $"GitHub does not accept {(eventType == "APPROVE" ? "an approval" : "a change request")} "
				+ "on your own pull request. Submit it as a comment instead; the drafts are kept.";
		}
		try
		{
			var (submitted, skipped) = await SubmitAsync(eventType, body);
			return $"Review submitted ({eventType}): {submitted} comment(s) posted"
				+ (skipped > 0 ? $", {skipped} kept local (outdated/off-diff)" : "") + ".";
		}
		catch (ToolFailedException ex)
		{
			return ex.Message;
		}
	}

	/// <summary>Submits drafts that sit on commentable diff lines as a review; drafts that
	/// don't (outdated or outside the diff) stay local. Returns (submitted, skipped).</summary>
	public async Task<(int Submitted, int Skipped)> SubmitAsync(string eventType, string body)
	{
		if (workspace.CurrentPr is not { } pr)
			return (0, 0);
		var payload = new List<ReviewCommentDto>();
		var replies = new List<(long InReplyTo, string Body, Guid Id)>();
		var submitted = new List<Guid>();
		int skipped = 0;
		foreach (var draft in Drafts)
		{
			// A reply belongs to a thread, not to a line: it goes as its own request and needs
			// nothing from the diff, so it survives the line it hung on moving or disappearing.
			if (draft.Stored.InReplyTo is { } inReplyTo)
			{
				replies.Add((inReplyTo, draft.Stored.Body, draft.Stored.Id));
				continue;
			}
			var anchor = draft.Stored.Anchor;
			var file = anchor.OldSide
				? workspace.Files.FirstOrDefault(f => f.OldPath == anchor.Path)
				: workspace.Files.FirstOrDefault(f => f.Path == anchor.Path);
			// A generated file has no counterpart in the pull request, so GitHub would reject
			// the whole review over it. Such a draft stays local, like an outdated one.
			bool ok = draft.CurrentLine is { } line && file is { IsGenerated: false }
				&& (anchor.OldSide
					? workspace.Changed.CommentableOld(file.OldPath).Contains(line)
					: workspace.Changed.CommentableNew(file.Path).Contains(line));
			if (!ok)
			{
				skipped++;
				continue;
			}
			payload.Add(new ReviewCommentDto(
				anchor.OldSide ? file!.OldPath : file!.Path,
				draft.CurrentLine!.Value,
				anchor.OldSide ? "LEFT" : "RIGHT",
				draft.Stored.Body));
			submitted.Add(draft.Stored.Id);
		}
		// An empty review is refused by GitHub, and a pass whose whole content is replies has
		// nothing to submit: the replies themselves are the review, and the first of them
		// carries the mark the review body would have.
		bool reviewSubmitted = payload.Count > 0 || body.Trim().Length > 0 || replies.Count == 0;
		if (reviewSubmitted)
			await workspace.GitHub.SubmitReviewAsync(pr.Number, new ReviewSubmission(body, eventType, payload));
		for (int i = 0; i < replies.Count; i++)
		{
			var (inReplyTo, replyBody, id) = replies[i];
			await workspace.GitHub.ReplyToCommentAsync(pr.Number, inReplyTo,
				!reviewSubmitted && i == 0 ? GitHubService.AttributedReply(replyBody) : replyBody);
			submitted.Add(id);
		}
		foreach (var id in submitted)
			workspace.Store.RemoveDraft(id);
		RebuildDrafts();
		await LoadPostedAsync(pr.Number, CancellationToken.None);
		return (submitted.Count, skipped);
	}
}
