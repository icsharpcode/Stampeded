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

	/// <summary>What Add current PR writes into the entry: whether the merge, whoever runs it,
	/// also takes the head branch away. It belongs here rather than in a dialog because the
	/// button has none - the queue is a list to add to, not a decision to confirm.</summary>
	[ObservableProperty]
	bool deleteBranch = DeleteBranchPreference.Load();
}

/// <summary>What an entry is doing, kept apart from the entry itself: the queue on the remote
/// records what was decided, not what some window is in the middle of finding out.</summary>
public sealed record MergeQueueNote(string Text, bool Working);

public sealed partial class MergeQueueRow(int position, MergeQueueEntry entry, bool locked, bool pending,
	bool departed = false) : ObservableObject
{
	public MergeQueueEntry Entry { get; } = entry;

	/// <summary>No longer in the queue: merged, closed or taken out. Kept on the list anyway,
	/// under the entries still waiting, with the reason in its note - an entry that vanished
	/// while the reader was looking elsewhere is the one thing a shared queue must not do.</summary>
	public bool Departed { get; } = departed;

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
			string where = Departed ? " - " : Pending ? " . " : $"{position,2}.";
			string line = $"{(locked ? ">" : " ")} {where} #{Entry.Pr,-5} {Entry.Title}";
			line = line.Length > 44 ? line[..41] + "..." : line.PadRight(44);
			// A method that also deletes reads as one word: the column is what happens when this
			// entry lands, and the branch going is part of that.
			return $"{line} {Entry.Method + (Entry.DeleteBranch ? "+del" : ""),-10} {Entry.By}";
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

	/// <summary>Entries that were in the queue and are not any more, kept to be shown with what
	/// became of them. Their reasons live in <see cref="notes"/> like every other row's.</summary>
	readonly Dictionary<int, MergeQueueEntry> departed = [];

	/// <summary>Pull requests this window is in the middle of taking out on purpose. They are
	/// not a disappearance to explain to the reader who asked for it.</summary>
	readonly HashSet<int> takenOutHere = [];
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
			// The same choice the merge dialog offers, remembered in the same place: a reader
			// who wants their branches tidied wants it whichever way the merge is reached.
			else if (e.PropertyName == nameof(MergeQueueState.DeleteBranch))
				DeleteBranchPreference.Save(State.DeleteBranch);
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
			State.Status = $"Could not read the queue: {ExternalTool.Explain(ex)}";
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
	public Task EnqueueCurrentAsync(string method, bool deleteBranch)
	{
		if (workspace.CurrentPr is not { } pr)
		{
			State.Status = "No pull request is open.";
			return Task.CompletedTask;
		}
		return EnqueueAsync(pr.Number, pr.Title, method, deleteBranch);
	}

	/// <summary>The same, for a pull request nobody has opened for review - the start page's
	/// list. Queueing one is a decision about the pull request, not about the review of it.</summary>
	public async Task EnqueueAsync(int number, string title, string method, bool deleteBranch)
	{
		// Only when this is the review that came out of a snapshot: another pull request's place
		// in the queue has nothing to do with how this window read that one.
		if (workspace.Offline && workspace.CurrentPr?.Number == number)
		{
			State.Status = "Offline: this review was opened from a snapshot, and a queue nobody "
				+ "can reach is not one to add to. Reload (F5) first.";
			return;
		}
		departed.Remove(number);
		if (shown.Find(number) is not null)
		{
			State.Status = $"#{number} is already in the queue.";
			if (!drained)
				State.Driving = true;
			return;
		}

		string me = await workspace.MergeQueue.HolderAsync();
		pending = new MergeQueueEntry(number, title, "", method, me, DateTimeOffset.UtcNow, deleteBranch);
		Note(number, "adding to the queue", working: true);
		Show(shown);
		State.Status = $"Adding #{number} to the queue...";

		using var scope = workspace.Busy.Begin($"Queueing #{number}");
		try
		{
			Note(number, "asking GitHub what it points at", working: true);
			var state = await workspace.GitHub.GetMergeStateAsync(number);
			// A draft is not up for merging, and queueing one only puts something in front of
			// everybody that can never reach the front. Ready for Review is the thing to press
			// first, and saying so is more use than queueing it and reporting a block every turn.
			if (state.IsDraft)
			{
				Give(number, $"#{number} is a draft, so it cannot be queued. "
					+ "Ready for Review takes it out of draft; queue it after that.");
				return;
			}
			if (state.HeadRefOid is not { Length: > 0 } head)
			{
				Give(number, $"GitHub did not say what #{number} points at; it cannot be queued.");
				return;
			}
			Note(number, "publishing to the remote", working: true);
			await workspace.MergeQueue.EnqueueAsync(number, title, head, method, deleteBranch);
			notes.Remove(number);
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
			Give(number, $"Could not queue #{number}: {ex.Message}");
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
			State.Status = $"Could not clear the lock: {ExternalTool.Explain(ex)}";
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
		// The rows of entries that have already left take no write to clear: they are this
		// window's record of what happened, not part of the queue on the remote.
		int gone = departed.Count;
		foreach (int pr in departed.Keys.ToList())
			notes.Remove(pr);
		departed.Clear();
		if (failed.Count == 0)
		{
			Show(shown);
			State.Status = gone > 0
				? $"Cleared {Count(gone)} that had left the queue."
				: "Nothing in the queue has failed.";
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
			{
				notes.Remove(pr);
				takenOutHere.Add(pr);
			}
			await LoadAsync();
			State.Status = $"Took {Count(prs.Count)} out of the queue.";
		}
		catch (ToolFailedException ex)
		{
			State.Status = $"Could not take them out: {ExternalTool.Explain(ex)}";
		}
	}

	static string Count(int n) => n == 1 ? "1 entry" : $"{n} entries";

	public async Task RemoveAsync(MergeQueueRow row)
	{
		// A row that has already left the queue is this window's record of it; taking it out is
		// dropping the record, and asking the remote to remove what is not there says nothing.
		if (row.Departed)
		{
			departed.Remove(row.Entry.Pr);
			notes.Remove(row.Entry.Pr);
			Show(shown);
			State.Status = $"Cleared the record of #{row.Entry.Pr}.";
			return;
		}
		try
		{
			await workspace.MergeQueue.RemoveAsync(row.Entry.Pr, "taken out by hand");
			notes.Remove(row.Entry.Pr);
			takenOutHere.Add(row.Entry.Pr);
			await LoadAsync();
		}
		catch (ToolFailedException ex)
		{
			State.Status = $"Could not take #{row.Entry.Pr} out: {ExternalTool.Explain(ex)}";
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
			State.Status = $"Could not move #{row.Entry.Pr}: {ExternalTool.Explain(ex)}";
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
			State.Status = $"Driving the queue failed: {ExternalTool.Explain(ex)}";
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
		// An entry that was here a moment ago and is not now was merged, closed or taken out -
		// by this window, by another reader's, or by the drainer workflow on GitHub. Which of
		// those it was is read off the queue's own history; until then the row says it is asking.
		foreach (var gone in shown.Entries.Where(e => document.Find(e.Pr) is null))
		{
			if (takenOutHere.Remove(gone.Pr))
				continue;
			departed[gone.Pr] = gone;
			Note(gone.Pr, "gone from the queue; reading why", working: true);
			ExplainDepartureAsync(gone.Pr).HandleExceptions();
		}
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
		// Under the queue, in the order they left it: they are what happened, not what is next.
		foreach (var entry in departed.Values)
			Items.Add(new MergeQueueRow(0, entry, locked: false, pending: false, departed: true));

		// Notes outlive the rows they were on: rebuilding the list must not wipe what the last
		// turn found out about an entry that is still in the queue, or what became of one that
		// is not.
		foreach (int gone in notes.Keys
			.Where(pr => document.Find(pr) is null && pending?.Pr != pr && !departed.ContainsKey(pr))
			.ToList())
		{
			notes.Remove(gone);
		}
		PaintNotes();

		State.Holder = Describe(document);
	}

	/// <summary>Puts the queue's own account of a departure on its row. The ref records the
	/// subject of every change made to it, so this answers for changes no window here made.</summary>
	async Task ExplainDepartureAsync(int pr)
	{
		string? why = await workspace.MergeQueue.WhyGoneAsync(pr);
		if (!departed.ContainsKey(pr))
			return;
		Note(pr, why ?? "no longer in the queue; its history does not say why", working: false);
		PaintNotes();
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
			// A row that has left the queue is history, whatever became of it; only an entry
			// still waiting its turn can be one that will not go.
			row.Failed = !row.Departed && note is { Working: false, Text.Length: > 0 };
		}
		RefreshCanEnqueue();
		State.HasEntries = shown.Entries.Count > 0;
		State.HasErrors = Items.Any(r => r.Failed) || departed.Count > 0;
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
