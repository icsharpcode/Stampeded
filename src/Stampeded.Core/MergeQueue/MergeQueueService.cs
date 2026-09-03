using System.Text.Json;

using Stampeded.Core.Git;
using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;

namespace Stampeded.Core.MergeQueue;

/// <summary>The queue as it stands on the remote, with the commit it was read from.</summary>
public sealed record MergeQueueSnapshot(string? Sha, MergeQueueDocument Document);

/// <summary>Where one entry has got to, while the driver is still working through the queue.
/// <paramref name="Working"/> separates something in flight, which is worth a spinner, from a
/// verdict that has been reached and will not change until the next turn.</summary>
public sealed record MergeQueueProgress(int Pr, string Note, bool Working);

/// <summary>What one turn of the driver did, and why it passed over the entries it passed over.</summary>
public sealed record MergeQueueDriveResult(string Status, IReadOnlyList<(int Pr, string Reason)> Blocked)
{
	public static MergeQueueDriveResult Say(string status) => new(status, []);
}

/// <summary>
/// A merge queue shared by every Stampeded reading the same repository, on any machine.
///
/// The clients never talk to each other and have no server of their own, so the queue lives on
/// the one thing they all reach: a ref on the remote. It points at a chain of commits with an
/// empty tree, each carrying the whole queue as its message and each parented on the state it
/// replaces. Writing is a plain push - which git refuses unless it fast-forwards - so a client
/// that read a state somebody else has since replaced is rejected by the server, atomically, and
/// retries against what it finds. That refusal is the compare-and-swap this depends on; there is
/// no lock server anywhere.
///
/// Because each state names its predecessor, `git log` on the ref is the queue's own history:
/// who enqueued what, who merged it, and when.
///
/// Nothing here is trusted to be exclusive. GitHub serializes the merges themselves - a second
/// `gh pr merge` of a pull request already merged is refused there - so the lock below only stops
/// duplicate work and says who is driving.
/// </summary>
/// <param name="identity">Who this client calls itself in the queue. Left out, it is asked of
/// gh once and remembered.</param>
public sealed class MergeQueueService(GitService git, GitHubService gitHub, string? identity = null)
{
	/// <summary>
	/// Where the queue lives on the remote. Outside refs/heads and refs/tags on purpose: no
	/// branch or tag list shows it, no clone fetches it by default, and no branch protection
	/// rule applies to it. GitHub accepts it like any other ref.
	/// </summary>
	public const string QueueRef = "refs/stampeded/merge-queue";

	/// <summary>
	/// How long a lock stays its holder's before anyone may take it over. The critical section
	/// is one `gh pr merge`, which takes seconds, so minutes of slack cover a client that died
	/// mid-merge while still absorbing the difference between two hosts' clocks - the timestamp
	/// is written by the holder's clock, and git offers no clock the two sides share.
	///
	/// ponytail: a fixed lease against unsynchronised clocks, sound only because a wrong steal
	/// is harmless here. A queue that waited for CI would hold the lock for as long as CI takes
	/// and would need a real heartbeat instead.
	/// </summary>
	public static readonly TimeSpan LeaseTime = TimeSpan.FromMinutes(5);

	/// <summary>How many times a rejected write is re-applied to what the winner left behind.</summary>
	const int WriteAttempts = 5;

	/// <summary>Tells this client's lock apart from that of another Stampeded run by the same
	/// person on the same machine, which the holder name alone cannot do.</summary>
	readonly string clientId = Guid.NewGuid().ToString("n")[..8];

	string? holder = identity;

	/// <summary>The queue as the remote has it. A missing ref is an empty queue, not an error:
	/// the first enqueue is what creates it.</summary>
	public async Task<MergeQueueSnapshot> ReadAsync(CancellationToken ct = default)
	{
		string listing = await Git(ct, "ls-remote", "origin", QueueRef);
		string first = listing.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
		if (first.Length == 0)
			return new MergeQueueSnapshot(null, MergeQueueDocument.Empty);

		string sha = first.Split('\t', ' ')[0].Trim();
		// The ref has to be local before its message can be read, and it is mirrored rather than
		// merged: the remote state replaces ours whatever ours was.
		await Git(ct, "fetch", "origin", $"+{QueueRef}:{QueueRef}");
		string commit = await Git(ct, "cat-file", "commit", sha);
		return new MergeQueueSnapshot(sha, Parse(commit));
	}

	/// <summary>
	/// Applies an edit to the queue and publishes it. <paramref name="edit"/> is handed the
	/// current document and returns the replacement plus the one-line subject that says what it
	/// did, or null to leave the queue alone. It may be called more than once: a push another
	/// client won is not an error, it is the signal to re-apply the edit to their result.
	/// </summary>
	public async Task<MergeQueueDocument> UpdateAsync(
		Func<MergeQueueDocument, (MergeQueueDocument Document, string Subject)?> edit,
		CancellationToken ct = default)
	{
		for (int attempt = 1; ; attempt++)
		{
			var snapshot = await ReadAsync(ct);
			if (edit(snapshot.Document) is not { } change)
				return snapshot.Document;

			ToolFailedException rejected;
			try
			{
				await PublishAsync(snapshot.Sha, change.Subject, change.Document, ct);
				CliLog.Write("mergequeue", change.Subject);
				return change.Document;
			}
			catch (ToolFailedException ex)
			{
				rejected = ex;
			}

			// Somebody else published between the read and the push: their state is now the one
			// to edit. Anything else - no push access, a rejecting hook - leaves the ref where we
			// found it and is reported rather than hammered at.
			if (attempt >= WriteAttempts || !await RaceLostAsync(snapshot.Sha, ct))
				throw rejected;
			CliLog.Write("mergequeue", $"queue changed under attempt {attempt}, re-applying");
		}
	}

	/// <summary>Adds a pull request at the back of the queue. Queueing one twice is not an
	/// error and does not move it: its place is the one it was given.</summary>
	public async Task<MergeQueueDocument> EnqueueAsync(
		int pr, string title, string headSha, string method, CancellationToken ct = default)
	{
		string by = await HolderAsync(ct);
		return await UpdateAsync(doc => doc.Find(pr) is not null
			? null
			: (doc.With([.. doc.Entries, new MergeQueueEntry(pr, title, headSha, method, by, DateTimeOffset.UtcNow)]),
				$"enqueue #{pr} by {by}"), ct);
	}

	/// <summary>Drops a pull request from the queue. Dropping one that is not in it is a no-op:
	/// somebody else got there first, which is the same outcome.</summary>
	public Task<MergeQueueDocument> RemoveAsync(int pr, string reason, CancellationToken ct = default)
		=> RemoveAsync([pr], reason, ct);

	/// <summary>
	/// Drops several at once. One write rather than one per entry: clearing a queue of six with
	/// six pushes would be six chances for somebody else's write to land in the middle, and the
	/// reader who asked for it gone would watch it go one at a time.
	/// </summary>
	public Task<MergeQueueDocument> RemoveAsync(
		IReadOnlyCollection<int> prs, string reason, CancellationToken ct = default)
		=> UpdateAsync(doc => {
			var going = doc.Entries.Where(e => prs.Contains(e.Pr)).ToList();
			if (going.Count == 0)
				return null;
			string what = going.Count == 1
				? $"#{going[0].Pr}"
				: $"{going.Count} entries ({string.Join(", ", going.Take(4).Select(e => "#" + e.Pr))}"
					+ (going.Count > 4 ? ", ...)" : ")");
			return (doc.With([.. doc.Entries.Where(e => !prs.Contains(e.Pr))]), $"remove {what}: {reason}");
		}, ct);

	/// <summary>Moves a queued pull request by <paramref name="delta"/> places.</summary>
	public Task<MergeQueueDocument> MoveAsync(int pr, int delta, CancellationToken ct = default)
		=> UpdateAsync(doc => {
			var entries = doc.Entries.ToList();
			int from = entries.FindIndex(e => e.Pr == pr);
			if (from < 0)
				return null;
			int to = Math.Clamp(from + delta, 0, entries.Count - 1);
			if (to == from)
				return null;
			var entry = entries[from];
			entries.RemoveAt(from);
			entries.Insert(to, entry);
			return (doc.With(entries), $"move #{pr} to position {to + 1}");
		}, ct);

	/// <summary>
	/// Claims the right to merge <paramref name="pr"/>, returning false when another client
	/// holds a lock that has not run out. Exactly one of several clients asking at once can
	/// succeed, because taking the lock is an ordinary write and only one write wins.
	/// </summary>
	public async Task<bool> TryAcquireAsync(int pr, CancellationToken ct = default)
	{
		string me = await HolderAsync(ct);
		bool taken = false;
		await UpdateAsync(doc => {
			taken = false;
			if (doc.Lock is { } held && held.Client != clientId && !held.IsExpired(LeaseTime))
				return null;
			taken = true;
			string how = doc.Lock is { } stale && stale.Client != clientId
				? $"lock taken from {stale.Holder} by {me}" : $"lock taken by {me}";
			return (doc with { Lock = new MergeQueueLock(me, clientId, DateTimeOffset.UtcNow, pr) },
				$"{how} for #{pr}");
		}, ct);
		return taken;
	}

	/// <summary>
	/// Clears whoever's lock is on the queue, ours or not. The lease already hands an abandoned
	/// lock on by itself, so this is for the case the lease cannot see: a client that is gone for
	/// good and a queue nobody wants to wait five minutes for, or a holder whose clock is wrong
	/// enough that its lock looks fresh forever.
	///
	/// Safe to offer because the lock was never what makes a merge exclusive - GitHub is. The
	/// worst a broken lock can do is let a second client attempt a merge that GitHub then refuses.
	/// Who broke it and whose it was both go in the subject, so the ref's history says it happened.
	/// </summary>
	/// <returns>The holder whose lock was cleared, or null when there was none.</returns>
	public async Task<string?> BreakLockAsync(CancellationToken ct = default)
	{
		string me = await HolderAsync(ct);
		string? broken = null;
		await UpdateAsync(doc => {
			broken = doc.Lock?.Holder;
			return doc.Lock is not { } held
				? null
				: (doc with { Lock = null },
					held.Client == clientId
						? $"lock released by {held.Holder}"
						: $"lock broken by {me}, was {held.Holder}'s for #{held.Pr}");
		}, ct);
		return broken;
	}

	/// <summary>Gives the lock back, if it is still ours to give.</summary>
	public Task<MergeQueueDocument> ReleaseAsync(CancellationToken ct = default)
		=> UpdateAsync(doc => doc.Lock is { } held && held.Client == clientId
			? (doc with { Lock = null }, $"lock released by {held.Holder}")
			: null, ct);

	/// <summary>
	/// One turn of the queue: find the first entry GitHub would merge, take the lock, merge it,
	/// and drop it. Entries it would not merge are passed over rather than left to block the
	/// ones behind them - a failing check at the front of the queue is one person's problem, not
	/// everybody's - and the reason is reported so the pane can say why.
	///
	/// An entry is dropped when its pull request is seen merged or closed, not when this merged
	/// it. That is what makes a client dying mid-merge harmless: its lock runs out, the next
	/// driver finds the pull request already merged, and drops the entry.
	/// </summary>
	public async Task<MergeQueueDriveResult> DriveOnceAsync(
		IProgress<MergeQueueProgress>? progress = null, CancellationToken ct = default)
	{
		var snapshot = await ReadAsync(ct);
		if (snapshot.Document.Entries.Count == 0)
			return MergeQueueDriveResult.Say("The queue is empty.");

		if (snapshot.Document.Lock is { } held && held.Client != clientId && !held.IsExpired(LeaseTime))
			return MergeQueueDriveResult.Say($"#{held.Pr} is being merged by {held.Holder}.");

		var blocked = new List<(int Pr, string Reason)>();
		foreach (var entry in snapshot.Document.Entries)
		{
			progress?.Report(new MergeQueueProgress(entry.Pr, "asking GitHub", Working: true));
			MergeState state;
			try
			{
				state = await gitHub.GetMergeStateAsync(entry.Pr, ct);
			}
			catch (ToolFailedException ex)
			{
				// A pull request GitHub will not even describe - deleted, or a number that was
				// never one - must not take the queue down with it. It is one entry's problem,
				// said beside that entry, and the turn carries on to the ones behind it.
				Pass(entry.Pr, ExternalTool.FailureReason(ex.StdErr, ""));
				continue;
			}

			if (state.State is "MERGED" or "CLOSED")
			{
				await RemoveAsync(entry.Pr, $"already {state.State!.ToLowerInvariant()}", ct);
				return new MergeQueueDriveResult($"#{entry.Pr} was already {state.State.ToLowerInvariant()}; dropped it.", blocked);
			}

			if (entry.HeadSha.Length > 0 && state.HeadRefOid is { Length: > 0 } head && head != entry.HeadSha)
			{
				Pass(entry.Pr, $"pushed to since it was queued ({entry.HeadSha[..Math.Min(7, entry.HeadSha.Length)]} -> {head[..7]}); queue it again");
				continue;
			}

			if (!state.CanMerge)
			{
				Pass(entry.Pr, state.Explain.ReplaceLineEndings(" ").Split(". ")[0]);
				continue;
			}

			if (!await TryAcquireAsync(entry.Pr, ct))
				return new MergeQueueDriveResult($"Another client took #{entry.Pr} first.", blocked);

			try
			{
				progress?.Report(new MergeQueueProgress(entry.Pr, $"merging ({entry.Method})", Working: true));
				await gitHub.MergePrAsync(entry.Pr, entry.Method, ct);
				CliLog.Write("mergequeue", $"merged #{entry.Pr} by {entry.Method}");
				await UpdateAsync(doc => (
					doc with {
						Entries = [.. doc.Entries.Where(e => e.Pr != entry.Pr)],
						Lock = doc.Lock is { } mine && mine.Client == clientId ? null : doc.Lock,
					},
					$"merged #{entry.Pr} by {entry.Method}"), ct);
				return new MergeQueueDriveResult($"Merged #{entry.Pr} ({entry.Method}).", blocked);
			}
			catch (ToolFailedException ex)
			{
				// The lock is given back rather than left to run out: the next turn should get to
				// try the entry behind this one straight away.
				await ReleaseAsync(ct);
				Pass(entry.Pr, ex.Message);
				return new MergeQueueDriveResult($"Merging #{entry.Pr} failed: {ex.Message}", blocked);
			}
		}

		return new MergeQueueDriveResult(
			$"Nothing in the queue can be merged right now ({blocked.Count} waiting).", blocked);

		void Pass(int pr, string reason)
		{
			blocked.Add((pr, reason));
			progress?.Report(new MergeQueueProgress(pr, reason, Working: false));
		}
	}

	/// <summary>
	/// Tells a drainer workflow, if this repository has one, that the queue has something in it,
	/// and answers whether there is one. A repository with a drainer needs no window left open
	/// and takes no turns from one: the workflow does the merging, GitHub serializes its runs,
	/// and a reader's Stampeded is only a view onto the same document.
	///
	/// Failing to send the event is not failing to queue. The workflow also runs on a schedule
	/// and on a finished check suite, so a lost nudge costs time rather than the entry.
	/// </summary>
	public async Task<bool> NudgeDrainerAsync(CancellationToken ct = default)
	{
		if (!await gitHub.HasMergeQueueWorkflowAsync(ct))
			return false;
		try
		{
			await gitHub.DispatchMergeQueueAsync(ct);
		}
		catch (ToolFailedException ex)
		{
			CliLog.Write("mergequeue", $"could not wake the drainer workflow: {ex.Message}");
		}
		return true;
	}

	/// <summary>Whether a workflow on GitHub empties this queue.</summary>
	public Task<bool> HasDrainerAsync(CancellationToken ct = default)
		=> gitHub.HasMergeQueueWorkflowAsync(ct);

	/// <summary>Who this client is, in the queue's own words. The GitHub login says which
	/// person and the machine name says which of their windows; without the login - offline, or
	/// no gh - the local account name is still better than nothing.</summary>
	public async Task<string> HolderAsync(CancellationToken ct = default)
	{
		if (holder is not null)
			return holder;
		string login;
		try
		{
			login = await gitHub.GetViewerLoginAsync(ct);
		}
		catch (ToolFailedException)
		{
			login = Environment.UserName;
		}
		return holder = $"{login}@{Environment.MachineName}";
	}

	public bool HoldsLock(MergeQueueDocument doc) => doc.Lock?.Client == clientId;

	async Task PublishAsync(string? parent, string subject, MergeQueueDocument doc, CancellationToken ct)
	{
		// git reads a tree listing from standard input, which is empty and immediately closed,
		// so this both names the empty tree and puts it in the object database - the pushed
		// commit has to point at a tree the remote can be given.
		string tree = (await Git(ct, "mktree")).Trim();
		string message = subject + "\n\n" + JsonSerializer.Serialize(doc, MergeQueueJson.Options);
		string[] args = parent is null
			? ["commit-tree", tree, "-m", message]
			: ["commit-tree", tree, "-p", parent, "-m", message];
		string commit = (await Git(ct, args)).Trim();
		// No --force and no lease: a queue state that does not descend from the one on the
		// remote is exactly what must not be published, and that is what git already refuses.
		await Git(ct, "push", "origin", $"{commit}:{QueueRef}");
	}

	/// <summary>Whether a rejected push was somebody else's write landing first. Asking the ref
	/// answers it exactly, where reading git's refusal would mean matching on its wording -
	/// a push refused for want of access leaves the ref where we found it.</summary>
	async Task<bool> RaceLostAsync(string? expected, CancellationToken ct)
	{
		try
		{
			return (await ReadAsync(ct)).Sha != expected;
		}
		catch (ToolFailedException)
		{
			return false;
		}
	}

	static MergeQueueDocument Parse(string commitObject)
	{
		// A commit object is headers, a blank line, then the message. Only the message is ours,
		// and the document starts at the brace after the subject line.
		string text = commitObject.ReplaceLineEndings("\n");
		int message = text.IndexOf("\n\n", StringComparison.Ordinal);
		string body = message < 0 ? text : text[(message + 2)..];
		int start = body.IndexOf('{');
		if (start < 0)
			return MergeQueueDocument.Empty;
		try
		{
			return JsonSerializer.Deserialize<MergeQueueDocument>(body[start..], MergeQueueJson.Options)
				?? MergeQueueDocument.Empty;
		}
		catch (JsonException ex)
		{
			// A queue nobody can read is worse than an empty one only if it is silently emptied.
			CliLog.Write("mergequeue", $"the queue on the remote does not parse: {ex.Message}");
			throw;
		}
	}

	Task<string> Git(CancellationToken ct, params string[] args)
		=> ExternalTool.RunAsync("git", args, git.RepoPath, ct);
}
