using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded;

/// <summary>
/// What of the review is being read: the whole change, one commit of it at a time, or only the
/// work that arrived since the reader's last pass. All three show a range of the same review,
/// so they share one way in and one way out - the button has always said "Whole change", and
/// that is what it means whichever narrowing was on.
///
/// The workspace owns what a review IS; this owns which part of it is on screen. It is given
/// the workspace because narrowing means re-reading the diff, re-keying the review state and
/// re-opening the tabs, none of which a scope can do for itself.
/// </summary>
public sealed class ReviewScopes(ReviewWorkspace workspace)
{
	/// <summary>
	/// The commit being read on its own, when the change is being worked through one
	/// commit at a time instead of as a single diff. A well-made series is the author's
	/// own decomposition of the change, and following it is usually easier than reading
	/// every logic change at once.
	/// </summary>
	public CommitInfo? Commit { get; private set; }

	/// <summary>The commits of the review, oldest first - the order they were written in.</summary>
	public IReadOnlyList<CommitInfo> Series { get; private set; } = [];

	public int CommitIndex { get; private set; }

	/// <summary>
	/// The tree the review is diffed against while only the work since the reader's last
	/// pass is in scope: everything they already read, replayed onto the current base. A
	/// tree and not a commit on purpose - after a rebase there is no commit whose diff to
	/// the head is the author's own edits, because the rebase mixed the new base into every
	/// one of them.
	/// </summary>
	public string? SinceLastPassBase { get; private set; }

	public bool InSinceLastPass => SinceLastPassBase is not null;

	/// <summary>True while the review is narrowed to anything less than the whole change.</summary>
	public bool InScope => Commit is not null || InSinceLastPass;

	/// <summary>What the reader is being shown, for the panes that head the file list. Empty
	/// when the whole change is in scope.</summary>
	public string ScopeLine { get; private set; } = "";

	public event Action? Changed;

	(string Base, string Head)? fullRange;

	/// <summary>The range the review is of. BaseSha and HeadSha stop describing it while a
	/// single commit is in scope - they move to that commit - so anything that talks about
	/// the review as a whole has to ask here instead.</summary>
	public (string Base, string Head)? ReviewRange
		=> fullRange ?? (workspace.BaseSha is { } b && workspace.HeadSha is { } h ? (b, h) : null);

	/// <summary>The range whose commits are being read. The since-last-pass scope diffs
	/// against a synthetic tree, which has no history, so its commits are the ones written
	/// since the previous pass - however the rewrite arranged them.</summary>
	public (string Base, string Head)? CommitRange
		=> InSinceLastPass && workspace.LastPassHead is { } previous && workspace.HeadSha is { } head
			? (previous, head)
			: workspace.BaseSha is { } b && workspace.HeadSha is { } h ? (b, h) : null;

	/// <summary>
	/// Why the change cannot be read one commit at a time, or null when it can. A control that
	/// is simply dead says nothing about itself, so the reason is what the control carries -
	/// and it is the same sentence <see cref="EnterCommitAsync"/> would answer with, so what is
	/// promised before the press and what is said after it cannot drift apart.
	/// </summary>
	public string? CommitScopeRefusal
	{
		get
		{
			if (workspace.HeadSha is null)
				return "No review is open.";
			if (workspace.DirtyWorktreePath is not null)
			{
				return "This review includes uncommitted work, which belongs to no commit - there is no "
					+ "series to step through.";
			}
			// Null until the series has been read: how many commits there are cannot be guessed,
			// and refusing on a count nobody has counted would disable the button on every open.
			return knownCommitCount switch {
				0 => "This review has no commits to step through.",
				1 => "This change is a single commit, so reading it commit by commit is reading the whole "
					+ "change - which is what you are looking at.",
				_ => null,
			};
		}
	}

	public bool CanEnterCommit => CommitScopeRefusal is null;

	/// <summary>What the commit-by-commit control says of itself: what it would do, or why it
	/// cannot do it here.</summary>
	public string CommitScopeTip => CommitScopeRefusal
		?? "Commit by commit - read the change one commit at a time, in the order it was written";

	/// <summary>Why the work since the reader's last pass cannot be read on its own, or null
	/// when it can.</summary>
	public string? SinceLastPassRefusal
	{
		get
		{
			if (workspace.LastPassHead is null)
			{
				return "No earlier pass is recorded for this review - Stampeded compares against the "
					+ "head you last opened it at, and this is the first.";
			}
			if (workspace.DirtyWorktreePath is not null)
			{
				return "This review includes uncommitted work, which was never part of a pass; "
					+ "there is nothing to compare it against.";
			}
			return ReviewRange is null ? "No review is open." : null;
		}
	}

	public bool CanEnterSinceLastPass => SinceLastPassRefusal is null;

	/// <summary>What the since-last-pass control says of itself: what it would show, or why
	/// there is nothing for it to show.</summary>
	public string SinceLastPassTip => SinceLastPassRefusal
		?? "Since last pass - only what changed since you last opened this review; after a rebase, "
			+ "the author's own edits without the commits it brought in";

	/// <summary>
	/// Forgets what was in scope. A review that has just been opened is the whole change by
	/// definition, and everything a scope holds belongs to the review it was entered from: the
	/// range it would return to, the commits it steps through, the tree it diffs against. Left
	/// behind, they describe a review that is no longer on screen, and the way out of a scope
	/// leads back to it.
	/// </summary>
	public void Reset()
	{
		cachedCommitsRange = null;
		cachedCommits = [];
		cachedCommitStats = null;
		knownCommitCount = null;
		Commit = null;
		Series = [];
		CommitIndex = 0;
		SinceLastPassBase = null;
		ScopeLine = "";
		sinceLastPassTree = null;
		fullRange = null;
	}

	#region The commits of the range

	(string Base, string Head)? cachedCommitsRange;
	IReadOnlyList<CommitInfo> cachedCommits = [];
	IReadOnlyDictionary<string, (int Added, int Removed)>? cachedCommitStats;

	/// <summary>How many commits the review has, once something has asked. Null before that:
	/// nothing counts them for their own sake, so what the count decides has to wait for the
	/// first reader of the series rather than pay for a log of its own.</summary>
	int? knownCommitCount;

	/// <summary>
	/// The commits of the review, newest first, fetched once per range. Everything that shows
	/// the series - the overview, the commits pane, the per-commit reader - was asking git for
	/// it separately, and asking again on every step through it, although stepping changes
	/// which commit is being read and not which commits there are. On a large repository that
	/// was most of a second per step.
	/// </summary>
	public async Task<IReadOnlyList<CommitInfo>> GetRangeCommitsAsync(CancellationToken ct = default)
	{
		if (ReviewRange is not { } range)
			return [];
		if (cachedCommitsRange != range)
		{
			cachedCommits = await workspace.Git.LogAsync(
				$"{range.Base}..{range.Head}", null, follow: false, limit: 200, ct);
			cachedCommitStats = null;
			cachedCommitsRange = range;
			// Whether reading the change commit by commit means anything is decided by how many
			// commits it has, and the control offering it is enabled from that - so the answer
			// has to be announced when it arrives, not only when a scope changes.
			if (knownCommitCount != cachedCommits.Count)
			{
				knownCommitCount = cachedCommits.Count;
				Changed?.Invoke();
			}
		}
		return cachedCommits;
	}

	/// <summary>
	/// The commits of a range. The review's own are the cache; a range inside it - the single
	/// commit being read in per-commit mode - is a slice of that same list, which is why the
	/// log carries each commit's parents. Anything else, such as the work since the last pass,
	/// starts at a tree no commit names and has to be asked for.
	/// </summary>
	public async Task<IReadOnlyList<CommitInfo>> GetCommitsAsync(
		(string Base, string Head) range, CancellationToken ct = default)
	{
		if (ReviewRange == range)
			return await GetRangeCommitsAsync(ct);
		var all = await GetRangeCommitsAsync(ct);
		int head = IndexOf(all, range.Head);
		if (head >= 0)
		{
			for (int i = head; i < all.Count; i++)
			{
				if (all[i].FirstParent is { } parent && SameCommit(parent, range.Base))
					return [.. all.Skip(head).Take(i - head + 1)];
			}
		}
		return await workspace.Git.LogAsync($"{range.Base}..{range.Head}", null, follow: false, limit: 200, ct);
	}

	static int IndexOf(IReadOnlyList<CommitInfo> commits, string sha)
	{
		for (int i = 0; i < commits.Count; i++)
		{
			if (SameCommit(commits[i].Sha, sha))
				return i;
		}
		return -1;
	}

	/// <summary>Whether two revisions name the same commit, either of them abbreviated: what a
	/// scope carries is whatever resolved it, and the log always answers in full.</summary>
	static bool SameCommit(string a, string b)
		=> a.Length >= b.Length
			? a.StartsWith(b, StringComparison.Ordinal)
			: b.StartsWith(a, StringComparison.Ordinal);

	/// <summary>Lines added and removed per commit of the range, from one pass over it.</summary>
	public async Task<IReadOnlyDictionary<string, (int Added, int Removed)>> GetRangeCommitStatsAsync(
		CancellationToken ct = default)
	{
		// The commits first: reading them is what decides whether the cache still describes
		// this range, and it drops the stats with them when it does not.
		await GetRangeCommitsAsync(ct);
		if (cachedCommitStats is { } cached)
			return cached;
		if (ReviewRange is not { } range)
			return new Dictionary<string, (int, int)>();
		return cachedCommitStats = await workspace.Git.GetCommitStatsAsync(range.Base, range.Head, ct);
	}

	#endregion

	#region One commit at a time

	/// <summary>Reads the review one commit at a time, starting at the oldest.</summary>
	public async Task EnterCommitAsync(int index = 0)
	{
		// The two scopes are alternatives, not layers: the since-last-pass scope diffs
		// against a tree, which has no history for a commit list to come from.
		if (InSinceLastPass)
			await ExitAsync();
		if (ReviewRange is not { } range)
			return;
		if (Series.Count == 0)
		{
			// Oldest first: the series is meant to be read in the order it was written.
			Series = [.. (await GetRangeCommitsAsync()).Reverse()];
		}
		// Asked after the series has been read, because that is when the count is known - a
		// change of one commit turns the offer down here and the control down through the same
		// answer.
		if (CommitScopeRefusal is { } refusal)
		{
			workspace.PostStatus(refusal);
			return;
		}
		fullRange ??= range;
		await ApplyCommitAsync(Math.Clamp(index, 0, Series.Count - 1));
	}

	public Task StepCommitAsync(int direction)
		=> Commit is null
			? Task.CompletedTask
			: ApplyCommitAsync(Math.Clamp(CommitIndex + direction, 0, Series.Count - 1));

	async Task ApplyCommitAsync(int index)
	{
		var commit = Series[index];
		CommitIndex = index;
		Commit = commit;
		// The parent came with the commit: asking git for it is a process per step, and the
		// log that listed the series already said what each one was written on top of.
		string parent = commit.FirstParent
			?? await workspace.Git.RevParseAsync($"{commit.Sha}^", CancellationToken.None);
		workspace.SetScopeContent(parent, commit.Sha, await workspace.Git.DiffAsync(parent, commit.Sha));
		workspace.Store.OpenCommitScope(Path.GetFileName(workspace.RepoPath), commit.Sha);
		// The semantic workspaces stay on the review's head: they describe where the code
		// ends up, which is the right frame for navigating out of a commit being read.
		await workspace.ApplyScopeSemanticsAsync();
		Changed?.Invoke();
		await workspace.RebuildForScopeAsync($"commit scope {index + 1}/{Series.Count} {commit.ShortSha}");
	}

	#endregion

	#region Only what changed since the last pass

	/// <summary>The replay is a pure function of (base, last pass head), so it is computed
	/// once and kept for as long as the review is open.</summary>
	string? sinceLastPassTree;

	/// <summary>Forgets the replayed tree: what it is worth depends on the head the last pass
	/// ended at, which is what re-reading the review's state has just re-established.</summary>
	public void ForgetSinceLastPassTree() => sinceLastPassTree = null;

	/// <summary>
	/// Narrows the review to what changed since the reader's last pass: the same scoping the
	/// per-commit reader gets, over the author's own edits rather than one commit.
	///
	/// Viewed flags, depth marks and drafts stay in the review's own state file, unlike the
	/// per-commit scope which keys its own. That is deliberate: this scope's head IS the
	/// review's head at the same revision, so a file read here has genuinely been read for
	/// the review - the same bargain the re-review carry-over already makes for the files a
	/// push did not touch.
	/// </summary>
	public async Task EnterSinceLastPassAsync()
	{
		if (SinceLastPassRefusal is { } refusal)
		{
			workspace.PostStatus(refusal);
			return;
		}
		if (InScope)
			await ExitAsync();
		if (workspace.LastPassHead is not { } previous || ReviewRange is not { } range)
			return;
		using var busy = workspace.Busy.Begin("Diffing against your last pass");
		if (sinceLastPassTree is null)
		{
			try
			{
				sinceLastPassTree = await workspace.Git.ReplayTreeAsync(range.Base, previous, workspace.LastPassBase);
			}
			catch (ToolFailedException ex)
			{
				workspace.PostStatus($"Diff since last pass failed: {ex.Message}");
				return;
			}
		}
		if (sinceLastPassTree is null)
		{
			workspace.PostStatus($"The work you read at {previous[..9]} does not replay onto {range.Base[..9]} "
				+ "without conflicts, so there is no clean diff of the author's edits alone. Showing the raw "
				+ "interdiff instead - it includes the commits the rebase brought in.");
			await workspace.OpenInterdiffAsync();
			return;
		}
		var files = await workspace.Git.DiffAsync(sinceLastPassTree, range.Head);
		if (files.Count == 0)
		{
			workspace.PostStatus($"Nothing has changed since your last pass at {previous[..9]}"
				+ (await workspace.Git.IsAncestorAsync(previous, range.Head) ? "." : " - the branch was only rebased."));
			return;
		}
		bool rewritten = !await workspace.Git.IsAncestorAsync(previous, range.Head);
		// Counted while the workspace still holds the whole change: a reader who works through a
		// scope where everything is ticked can otherwise approve a change they never read, and
		// this is what the review still owes them.
		int wholeChange = workspace.Files.Count;
		int neverViewed = workspace.Files.Count(f => !workspace.Store.IsViewed(f.Path));
		fullRange ??= range;
		SinceLastPassBase = sinceLastPassTree;
		workspace.SetScopeContent(sinceLastPassTree, range.Head, files);
		ScopeLine = $"Since your pass at {previous[..9]}{(rewritten ? " (head rewritten)" : "")}: "
			+ $"{files.Count} file(s). Whole change: {neverViewed} of {wholeChange} file(s) never viewed.";
		await workspace.ApplyScopeSemanticsAsync();
		workspace.PostStatus(ScopeLine);
		Changed?.Invoke();
		await workspace.RebuildForScopeAsync($"since-last-pass scope {previous[..9]} -> {range.Head[..9]} "
			+ $"({(rewritten ? "rewritten" : "fast-forward")}), base tree {sinceLastPassTree[..9]}, {files.Count} file(s)");
	}

	#endregion

	/// <summary>Back to reading the whole change at once, out of whichever scope was on.
	/// One exit for both: the button has always said "Whole change", and that is what it
	/// means whether a commit or the work since the last pass was being read.</summary>
	public async Task ExitAsync()
	{
		if (fullRange is not { } range)
			return;
		Commit = null;
		SinceLastPassBase = null;
		ScopeLine = "";
		workspace.ClearScopeSemantics();
		fullRange = null;
		workspace.SetScopeContent(range.Base, range.Head,
			workspace.DirtyWorktreePath is { } dirty
				? await workspace.Git.DiffWorkingTreeAsync(dirty, range.Base)
				: await workspace.Git.DiffAsync(range.Base, range.Head));
		if (workspace.CurrentPr is { } pr)
			workspace.Store.Open(Path.GetFileName(workspace.RepoPath), pr.Number, range.Head, range.Base);
		else
			// Keyed by the refs the review was opened with, exactly as OpenLocalRangeAsync
			// keyed it: a key built from SHAs instead names a state file nobody wrote, and
			// the review's own - its viewed flags, depth marks and drafts - is orphaned.
			workspace.Store.OpenLocal(
				Path.GetFileName(workspace.RepoPath), LocalRangeKey(range), range.Head, range.Base);
		Changed?.Invoke();
		await workspace.RebuildForScopeAsync("scope off");
	}

	/// <summary>The key a local review's state file is named by: the range as it was opened,
	/// falling back to the resolved commits when the review was not opened from refs.</summary>
	string LocalRangeKey((string Base, string Head) range)
		=> workspace.LocalRange is { } local
			? $"{local.Base}..{local.Head}"
			: $"{range.Base[..9]}..{range.Head[..9]}";
}
