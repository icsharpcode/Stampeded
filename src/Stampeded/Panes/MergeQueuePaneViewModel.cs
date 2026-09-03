using System.Collections.ObjectModel;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;
using Stampeded.Core.MergeQueue;

namespace Stampeded.Panes;

public sealed partial class MergeQueueState : ObservableObject
{
	[ObservableProperty]
	string status = "Open a pull request to add it to the merge queue.";

	/// <summary>Who is merging right now, or what becomes of the queue if nobody is.</summary>
	[ObservableProperty]
	string holder = "";

	/// <summary>Whether this window is taking turns at the head of the queue. Off, the pane is
	/// a list somebody refreshes; on, it is the thing that merges.</summary>
	[ObservableProperty]
	bool driving;

	/// <summary>What the toolbar may act on. A button that would do nothing says so by being
	/// unavailable, rather than by being pressed and reporting that there was nothing to do.</summary>
	[ObservableProperty]
	bool hasEntries;

	[ObservableProperty]
	bool hasErrors;

	[ObservableProperty]
	bool hasLock;

	/// <summary>There is a pull request open that could go in the queue. A draft could not, and
	/// a button that can be pressed to be told so is a button that lied.</summary>
	[ObservableProperty]
	bool canEnqueue;
}

/// <summary>What an entry is doing, kept apart from the entry itself: the queue on the remote
/// records what was decided, not what some window is in the middle of finding out.</summary>
public sealed record MergeQueueNote(string Text, bool Working);

public sealed partial class MergeQueueRow(int position, MergeQueueEntry entry, bool locked, bool pending)
	: ObservableObject
{
	public MergeQueueEntry Entry { get; } = entry;

	/// <summary>Put here by this window and not yet on the remote. Shown at once anyway: the
	/// round trip takes a second or two, and a list that stays empty that long reads as a button
	/// that did nothing.</summary>
	public bool Pending { get; } = pending;

	/// <summary>The right-hand column: a spinner and what is happening, or the verdict the last
	/// turn reached. Empty for an entry that is simply waiting its turn.</summary>
	[ObservableProperty]
	string note = "";

	/// <summary>The note is a reason this entry did not merge, rather than something in progress.
	/// Every note that has settled is one: the driver only writes a note to say why it passed an
	/// entry over, so "not working any more" and "went wrong" are the same state here.</summary>
	[ObservableProperty]
	bool failed;

	public string Display
	{
		get
		{
			// A fixed title column keeps the columns behind it lined up, which is what makes a
			// queue scannable; narrow enough that an ordinary pane width needs no scrolling.
			string where = Pending ? " . " : $"{position,2}.";
			string line = $"{(locked ? ">" : " ")} {where} #{Entry.Pr,-5} {Entry.Title}";
			line = line.Length > 44 ? line[..41] + "..." : line.PadRight(44);
			return $"{line} {Entry.Method,-6} {Entry.By}";
		}
	}
}

/// <summary>
/// The merge queue every Stampeded on this repository shares. The list itself lives on a ref on
/// the remote (<see cref="MergeQueueService"/>); this pane reads it, changes it, and - while
/// Drive is on - takes turns merging what is at the front of it.
///
/// Drive is the only thing in this application that polls. Everything else refreshes because
/// something happened or because somebody asked; a queue shared with people on other machines is
/// the one case where nothing local will ever say that it changed.
/// </summary>
public partial class MergeQueuePaneViewModel : Tool
{
	/// <summary>Long enough that a driving window is not a load on the remote, short enough that
	/// a merge somebody queued lands while they are still looking at the pane.</summary>
	static readonly TimeSpan DriveInterval = TimeSpan.FromSeconds(30);

	readonly ReviewWorkspace workspace;
	readonly DispatcherTimer timer;
	readonly Dictionary<int, MergeQueueNote> notes = [];

	/// <summary>Queued by this window a moment ago and not yet read back off the remote.</summary>
	MergeQueueEntry? pending;
	MergeQueueDocument shown = MergeQueueDocument.Empty;

	/// <summary>Whether a workflow on GitHub empties this queue, once it has been asked.</summary>
	bool drained;
	bool busy;

	public ObservableCollection<MergeQueueRow> Items { get; } = [];
	public MergeQueueState State { get; } = new();

	public MergeQueuePaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		timer = new DispatcherTimer { Interval = DriveInterval };
		timer.Tick += (_, _) => DriveAsync().HandleExceptions();
		State.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(MergeQueueState.Driving))
				OnDrivingChanged();
		};
		// The application already animates one spinner for everything that takes a while. A clock
		// of our own would run beside it a few milliseconds out of step, for no gain.
		workspace.Busy.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(BusyTracker.SpinnerFrame))
				PaintNotes();
		};
		workspace.ReviewChanged += () => LoadAsync().HandleExceptions();
		// Marking a pull request ready is the one thing that turns Add current PR from refused
		// into available without the review being reloaded.
		workspace.PrStateChanged += RefreshCanEnqueue;
	}

	/// <summary>Whether the open pull request is one the queue would take. Read from what the
	/// review already holds rather than asked of GitHub: this runs on every load and on every
	/// spinner frame, and a round trip for a flag the review was given at open is waste.</summary>
	void RefreshCanEnqueue()
		=> State.CanEnqueue = workspace.CurrentPr is { IsDraft: false } && !workspace.Offline;

	/// <summary>Reads the queue off the remote. One ls-remote and one fetch however long the
	/// queue is: it is a single document.</summary>
	public async Task LoadAsync()
	{
		if (busy)
			return;
		busy = true;
		try
		{
			var snapshot = await workspace.MergeQueue.ReadAsync();
			drained = await workspace.MergeQueue.HasDrainerAsync();
			Show(snapshot.Document);
			string count = snapshot.Document.Entries.Count == 0
				? "The queue is empty."
				: $"{snapshot.Document.Entries.Count} queued.";
			// A disabled button shows no tooltip, and "why can I not add this one" is exactly
			// the question the reader has when Add current PR is greyed out.
			State.Status = workspace.CurrentPr is { IsDraft: true, Number: var draft }
				? $"{count} #{draft} is a draft, so it cannot be queued - Ready for Review first."
				: count;
		}
		catch (ToolFailedException ex)
		{
			State.Status = $"Could not read the queue: {ex.Message}";
		}
		finally
		{
			busy = false;
		}
	}

	/// <summary>
	/// Adds the pull request being reviewed, and starts driving: queueing something is asking for
	/// it to be merged, and a window that asked and then waited for somebody to press Drive is
	/// the queue looking stuck for no reason.
	///
	/// The row appears before the remote knows about it. Its head is read fresh rather than taken
	/// from the review, because what goes in the queue has to be the revision GitHub would merge
	/// and not the one this window happens to be showing.
	/// </summary>
	public async Task EnqueueCurrentAsync(string method)
	{
		if (workspace.CurrentPr is not { } pr)
		{
			State.Status = "No pull request is open.";
			return;
		}
		if (workspace.Offline)
		{
			State.Status = "Offline: this review was opened from a snapshot, and a queue nobody "
				+ "can reach is not one to add to. Reload (F5) first.";
			return;
		}
		if (shown.Find(pr.Number) is not null)
		{
			State.Status = $"#{pr.Number} is already in the queue.";
			if (!drained)
				State.Driving = true;
			return;
		}

		string me = await workspace.MergeQueue.HolderAsync();
		pending = new MergeQueueEntry(pr.Number, pr.Title, "", method, me, DateTimeOffset.UtcNow);
		Note(pr.Number, "adding to the queue", working: true);
		Show(shown);
		State.Status = $"Adding #{pr.Number} to the queue...";

		using var scope = workspace.Busy.Begin($"Queueing #{pr.Number}");
		try
		{
			Note(pr.Number, "asking GitHub what it points at", working: true);
			var state = await workspace.GitHub.GetMergeStateAsync(pr.Number);
			// A draft is not up for merging, and queueing one only puts something in front of
			// everybody that can never reach the front. Ready for Review is the thing to press
			// first, and saying so is more use than queueing it and reporting a block every turn.
			if (state.IsDraft)
			{
				Give(pr.Number, $"#{pr.Number} is a draft, so it cannot be queued. "
					+ "Ready for Review takes it out of draft; queue it after that.");
				return;
			}
			if (state.HeadRefOid is not { Length: > 0 } head)
			{
				Give(pr.Number, $"GitHub did not say what #{pr.Number} points at; it cannot be queued.");
				return;
			}
			Note(pr.Number, "publishing to the remote", working: true);
			await workspace.MergeQueue.EnqueueAsync(pr.Number, pr.Title, head, method);
			notes.Remove(pr.Number);
			pending = null;
			// A repository with a drainer gets an event instead of a driver: the workflow merges
			// whether or not this window stays open, and two things draining one queue would only
			// take turns being refused by GitHub.
			drained = await workspace.MergeQueue.NudgeDrainerAsync();
			await LoadAsync();
			if (!drained)
				State.Driving = true;   // last: turning it on runs a turn, which should see the entry
		}
		catch (ToolFailedException ex)
		{
			Give(pr.Number, $"Could not queue #{pr.Number}: {ex.Message}");
		}

		void Give(int number, string message)
		{
			pending = null;
			notes.Remove(number);
			Show(shown);
			State.Status = message;
		}
	}

	/// <summary>
	/// Takes the lock off the queue whoever holds it. Waiting out the lease is the ordinary path;
	/// this is for the window that is not coming back and the reader who knows it. Breaking a lock
	/// that has not run out is somebody else's merge, so that case asks first - a stale one does
	/// not, because there is nothing left to interrupt.
	/// </summary>
	public async Task BreakLockAsync()
	{
		if (shown.Lock is not { } held)
		{
			State.Status = "Nothing holds the queue.";
			return;
		}

		bool mine = workspace.MergeQueue.HoldsLock(shown);
		if (!mine && !held.IsExpired(MergeQueueService.LeaseTime)
			&& ReviewWorkspace.MainWindowOrNull() is { } owner)
		{
			bool go = await new ConfirmWindow("Clear the merge queue's lock",
				$"{held.Holder} took the lock for #{held.Pr} {Ago(held.At)} ago and it has not run "
					+ "out yet, so that window may still be merging.\n\n"
					+ "Clearing it lets this window - or any other - start on the queue as well. "
					+ "GitHub refuses a second merge of one pull request, so the worst case is a "
					+ "failed attempt rather than a double merge.",
				"Clear lock").ShowDialog<bool>(owner);
			if (!go)
				return;
		}

		try
		{
			string? broken = await workspace.MergeQueue.BreakLockAsync();
			await LoadAsync();
			State.Status = broken is null ? "Nothing holds the queue."
				: $"Cleared {(mine ? "this window's" : broken + "'s")} lock on #{held.Pr}.";
		}
		catch (ToolFailedException ex)
		{
			State.Status = $"Could not clear the lock: {ex.Message}";
		}
	}

	/// <summary>
	/// Empties the queue. The queue belongs to everyone reading this repository, so this asks
	/// first: the entries somebody else put there go too, and they will not be told.
	/// </summary>
	public async Task ClearAsync()
	{
		if (shown.Entries.Count == 0)
		{
			State.Status = "The queue is already empty.";
			return;
		}
		if (ReviewWorkspace.MainWindowOrNull() is { } owner)
		{
			string me = await workspace.MergeQueue.HolderAsync();
			int mine = shown.Entries.Count(e => e.By == me);
			bool go = await new ConfirmWindow("Empty the merge queue",
				$"{Count(shown.Entries.Count)} would be taken out, "
					+ (mine == shown.Entries.Count
						? "all of them queued from this window."
						: $"{shown.Entries.Count - mine} of them queued by somebody else.\n\n"
							+ "The queue is shared with everyone reading this repository, and nothing "
							+ "tells them it was emptied.")
					+ "\n\nNothing is merged and nothing is closed; they simply stop being queued.",
				"Empty the queue").ShowDialog<bool>(owner);
			if (!go)
				return;
		}
		await RemoveManyAsync([.. shown.Entries.Select(e => e.Pr)], "queue emptied by hand");
	}

	/// <summary>Takes out the entries the last turn could not merge, leaving the ones still
	/// waiting their turn. No question asked: it removes only what already said it was going
	/// nowhere.</summary>
	public async Task ClearErrorsAsync()
	{
		var failed = shown.Entries.Where(e => notes.TryGetValue(e.Pr, out var n) && !n.Working)
			.Select(e => e.Pr).ToList();
		if (failed.Count == 0)
		{
			State.Status = "Nothing in the queue has failed.";
			return;
		}
		await RemoveManyAsync(failed, "could not be merged");
	}

	async Task RemoveManyAsync(IReadOnlyList<int> prs, string reason)
	{
		try
		{
			await workspace.MergeQueue.RemoveAsync(prs, reason);
			foreach (int pr in prs)
				notes.Remove(pr);
			await LoadAsync();
			State.Status = $"Took {Count(prs.Count)} out of the queue.";
		}
		catch (ToolFailedException ex)
		{
			State.Status = $"Could not take them out: {ex.Message}";
		}
	}

	static string Count(int n) => n == 1 ? "1 entry" : $"{n} entries";

	public async Task RemoveAsync(MergeQueueRow row)
	{
		try
		{
			await workspace.MergeQueue.RemoveAsync(row.Entry.Pr, "taken out by hand");
			notes.Remove(row.Entry.Pr);
			await LoadAsync();
		}
		catch (ToolFailedException ex)
		{
			State.Status = $"Could not take #{row.Entry.Pr} out: {ex.Message}";
		}
	}

	public async Task MoveAsync(MergeQueueRow row, int delta)
	{
		try
		{
			await workspace.MergeQueue.MoveAsync(row.Entry.Pr, delta);
			await LoadAsync();
		}
		catch (ToolFailedException ex)
		{
			State.Status = $"Could not move #{row.Entry.Pr}: {ex.Message}";
		}
	}

	/// <summary>One turn at the front of the queue: merge what can be merged, and say beside each
	/// of the rest why it was passed over. Also the pane's refresh while Drive is on.</summary>
	public async Task DriveAsync()
	{
		if (busy)
			return;
		busy = true;
		using var scope = workspace.Busy.Begin("Merge queue");
		try
		{
			// Each entry says where it has got to as the turn reaches it, rather than the whole
			// list sitting blank until every one of them has been asked about.
			var progress = new Progress<MergeQueueProgress>(step => {
				Note(step.Pr, step.Note, step.Working);
				PaintNotes();
			});
			var result = await workspace.MergeQueue.DriveOnceAsync(progress);
			Show((await workspace.MergeQueue.ReadAsync()).Document);
			State.Status = result.Status;
		}
		catch (ToolFailedException ex)
		{
			State.Status = $"Driving the queue failed: {ex.Message}";
		}
		finally
		{
			busy = false;
		}
	}

	void OnDrivingChanged()
	{
		if (State.Driving)
		{
			if (workspace.Offline)
			{
				State.Status = "Offline: nothing can be merged from a snapshot. Reload (F5) first.";
				State.Driving = false;
				return;
			}
			timer.Start();
			DriveAsync().HandleExceptions();
		}
		else
		{
			timer.Stop();
		}
		State.Holder = Describe(shown);
	}

	void Note(int pr, string text, bool working) => notes[pr] = new MergeQueueNote(text, working);

	void Show(MergeQueueDocument document)
	{
		shown = document;
		Items.Clear();
		int position = 1;
		foreach (var entry in document.Entries)
			Items.Add(new MergeQueueRow(position++, entry, document.Lock?.Pr == entry.Pr, pending: false));
		// A pull request read back off the remote is no longer pending, whoever put it there.
		if (pending is { } waiting && document.Find(waiting.Pr) is null)
			Items.Add(new MergeQueueRow(0, waiting, locked: false, pending: true));
		else
			pending = null;

		// Notes outlive the rows they were on: rebuilding the list must not wipe what the last
		// turn found out about an entry that is still in the queue.
		foreach (int gone in notes.Keys.Where(pr => document.Find(pr) is null && pending?.Pr != pr).ToList())
			notes.Remove(gone);
		PaintNotes();

		State.Holder = Describe(document);
	}

	/// <summary>Puts the current spinner frame in front of every note still waiting on something.
	/// Runs on every frame, so it does the least it can and touches no row whose note is already
	/// what it should be.</summary>
	void PaintNotes()
	{
		string frame = workspace.Busy.SpinnerFrame;
		foreach (var row in Items)
		{
			string text = notes.TryGetValue(row.Entry.Pr, out var note)
				? note.Working ? $"{frame} {note.Text}" : note.Text
				: "";
			if (row.Note != text)
				row.Note = text;
			row.Failed = note is { Working: false, Text.Length: > 0 };
		}
		RefreshCanEnqueue();
		State.HasEntries = shown.Entries.Count > 0;
		State.HasErrors = Items.Any(r => r.Failed);
		State.HasLock = shown.Lock is not null;
	}

	/// <summary>
	/// What is going to happen to the queue, which is not the same question as who holds the lock.
	/// The lock is only taken for the seconds a merge lasts, so an empty lock says nothing about
	/// whether anybody is watching - and a queue nobody is draining looks exactly like an idle one
	/// unless it says otherwise. This window can only answer for itself, and does.
	/// </summary>
	string Describe(MergeQueueDocument document)
	{
		if (document.Lock is { } held)
		{
			// Whose lock it is decides what the reader can do about it, so it is the first thing
			// the line says. "bob@mac" is not an answer when the reader is bob@mac in another
			// window - the client id is what tells those two apart, and only the service knows it.
			bool mine = workspace.MergeQueue.HoldsLock(document);
			string whose = mine ? "this window" : held.Holder;
			return held.IsExpired(MergeQueueService.LeaseTime)
				? $"#{held.Pr} has been locked by {whose} for {Ago(held.At)} with no sign of finishing; "
					+ "the lock is stale and any window may take it, or Clear lock takes it now."
				: mine
					? $"You are merging #{held.Pr} in this window, started {Ago(held.At)} ago."
					: $"#{held.Pr} is being merged by {held.Holder} - another window, not this one - "
						+ $"started {Ago(held.At)} ago.";
		}
		// Before the empty case, because "empty" and "empty, and something is watching it" are
		// different answers to what happens next - which is the whole question this line exists
		// to answer.
		if (drained)
		{
			return document.Entries.Count == 0
				? "Empty. A workflow on GitHub merges what is queued here, so nothing has to stay open."
				: $"A workflow on GitHub is merging these; #{document.Entries[0].Pr} has waited "
					+ $"{Ago(document.Entries[0].At)}. Nothing has to stay open.";
		}
		if (document.Entries.Count == 0 && pending is null)
			return "The queue is empty.";
		if (State.Driving)
			return $"Driving: this window takes the front of the queue every {DriveInterval.TotalSeconds:0}s.";
		if (document.Entries.Count == 0)
			return "";
		return $"Nobody is merging from this window - Drive is off, and #{document.Entries[0].Pr} "
			+ $"has waited {Ago(document.Entries[0].At)}. Turn Drive on here, or leave it to a window that has.";
	}

	static string Ago(DateTimeOffset when)
	{
		var age = DateTimeOffset.UtcNow - when;
		if (age < TimeSpan.FromMinutes(1))
			return $"{(int)age.TotalSeconds}s";
		return age < TimeSpan.FromHours(1) ? $"{(int)age.TotalMinutes}m" : $"{(int)age.TotalHours}h{age.Minutes}m";
	}
}
