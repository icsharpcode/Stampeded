using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Decompilation;
using Stampeded.Core.Diff;
using Stampeded.Core.Git;
using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;
using Stampeded.Core.Review;
using Stampeded.Core.Roslyn;
using Stampeded.Core.Testing;
using Stampeded.Documents;
using Stampeded.Navigation;

namespace Stampeded;

sealed record NavEntry(string DockableId, int BlobLine, bool OldSide) : IEquatable<NavEntry?>;

public sealed record ReferenceItem(string RelPath, int Line, string Preview, bool InChangedLine, bool OldSide);

/// <summary>
/// The open review session: which PR, its base/head SHAs, its file diffs, the semantic
/// workspace over the head worktree, and the review-progress store. Orchestrates git/gh
/// access, document opening and cross-document navigation.
/// </summary>
public sealed class ReviewWorkspace(string repoPath)
{
	public string RepoPath { get; } = repoPath;
	public GitService Git { get; } = new(repoPath);
	public GitHubService GitHub { get; } = new(repoPath);
	public WorktreeManager Worktrees { get; } = new(repoPath);

	/// <summary>File content at any revision, without a checkout: what the base side of a
	/// review is read from.</summary>
	public GitBlobReader Blobs { get; } = new(repoPath);
	public ReviewStateStore Store { get; } = new();
	public BusyTracker Busy { get; } = new();

	/// <summary>The review's comments, drafted and posted, and which part of the change is
	/// being read. Not field initializers: both are given this workspace, which an initializer
	/// cannot name.</summary>
	ReviewComments? comments;
	public ReviewComments Comments => comments ??= new(this);

	ReviewScopes? scopes;
	public ReviewScopes Scopes => scopes ??= new(this);

	// Set by MainViewModel once the layout exists.
	public Docking.StampededDockFactory? Factory { get; set; }
	public DocumentDock? Documents { get; set; }

	public PrDetail? CurrentPr { get; private set; }

	/// <summary>Where "#1234" in any text of this review points; null off GitHub.</summary>
	public string? IssueUrlPrefix { get; private set; }

	/// <summary>The refs a local range review was opened with, null for a pull request one.
	/// Worth keeping apart from <see cref="BaseSha"/>: that is their merge base, the commit
	/// the diff is really against, which is not what the user typed.</summary>
	public (string Base, string Head)? LocalRange { get; private set; }

	public string? BaseSha { get; private set; }
	public string? HeadSha { get; private set; }
	public IReadOnlyList<FileDiff> Files { get; private set; } = [];
	public RoslynWorkspaceService? Semantics { get; private set; }
	public RoslynWorkspaceService? BaseSemantics { get; private set; }

	/// <summary>Per worktree-relative file: line -> hit count, from the last coverage run.</summary>
	public IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>>? Coverage { get; private set; }

	/// <summary>Latest CI check runs, published by the Checks pane for the guide.</summary>
	public IReadOnlyList<CheckRun>? Checks { get; private set; }

	public void SetChecks(IReadOnlyList<CheckRun> checks)
	{
		Checks = checks;
		if (snapshot is { } current && checks is not null)
			KeepSnapshot(current with { Checks = checks });
		ChecksLoaded?.Invoke();
	}

	/// <summary>Where each reviewer stands, or null before the answer has arrived (and for a
	/// local review, which has no reviewers).</summary>
	public IReadOnlyList<ReviewVerdict>? Reviewers { get; private set; }

	public event Action? ReviewersChanged;

	async Task LoadReviewersAsync(int number, CancellationToken ct)
	{
		try
		{
			Reviewers = ReviewVerdicts.Latest(await GitHub.GetReviewsAsync(number, ct));
		}
		catch (ToolFailedException ex)
		{
			// Who has approved is worth knowing, and not knowing it is worth saying: an empty
			// list would read as nobody having reviewed.
			CliLog.Write("gh", $"reviews unavailable: {ex.Message}");
			Reviewers = null;
		}
		ReviewersChanged?.Invoke();
	}

	/// <summary>Set by the guide pane: whether approval should be allowed right now.</summary>
	public Func<(bool Ok, string Detail)>? ApprovalGate { get; set; }

	public (int Uncovered, int Measured) UncoveredAddedForFile(string path)
		=> Coverage is { } coverage && coverage.TryGetValue(path, out var hits)
			? CountUncovered(changed.Added(path), hits)
			: (0, 0);

	public bool IsUncoveredAdded(string path, int newLine)
		=> changed.IsAdded(path, newLine)
			&& Coverage is { } coverage && coverage.TryGetValue(path, out var hits)
			&& hits.TryGetValue(newLine, out int h) && h == 0;

	/// <summary>How many of the given lines the run never executed, out of those it measured
	/// at all. A line the report does not mention carries no instructions, so it is neither
	/// covered nor uncovered and counts as neither.</summary>
	static (int Uncovered, int Measured) CountUncovered(
		IEnumerable<int> lines, IReadOnlyDictionary<int, int> hits)
	{
		int uncovered = 0, measured = 0;
		foreach (int line in lines)
		{
			if (!hits.TryGetValue(line, out int count))
				continue;
			measured++;
			if (count == 0)
				uncovered++;
		}
		return (uncovered, measured);
	}

	/// <summary>
	/// The decompiler test cases this change touches, by the name their fixtures are built
	/// under - which is also the name of the test method that runs each one. Empty for a
	/// repository without that layout.
	/// </summary>
	public IReadOnlyList<string> AffectedFixtureNames
		=> [.. FixtureAssemblies.AffectedFixtures(Files.Select(f => f.Path))
			.Select(fixture => fixture.Name)
			.Distinct(StringComparer.Ordinal)];

	/// <summary>
	/// The tests worth running for this change, as names a test filter can match.
	///
	/// Two ways of arriving at them, and the first is not a guess. A changed decompiler test
	/// case is answered by name: the suite compiles the file and runs a test called after it,
	/// so the file says which test covers it. Nothing refers to what such a file declares, and
	/// tracing references from it only ever finds the file itself - which produced the right
	/// answer by coincidence of naming rather than because anything asked.
	///
	/// Everything else is traced: the members the change touches, and the test files that
	/// reference them. That is an inference and stays capped, because a change to a type
	/// everything uses would otherwise name every test there is.
	/// </summary>
	public async Task<IReadOnlyList<string>> SuggestImpactedTestClassesAsync()
	{
		var fixtures = AffectedFixtureNames;
		if (Semantics is not { State: SemanticState.Ready or SemanticState.SyntaxOnly } sem)
		{
			CliLog.Write("impacted", $"{fixtures.Count} test case(s) named directly; semantics not loaded, "
				+ "so nothing was traced");
			return fixtures;
		}
		var classes = new HashSet<string>(StringComparer.Ordinal);
		int traced = 0, unresolved = 0;
		// The fixture sources are already answered, so the tracing budget goes to the rest of
		// the change - which on a decompiler branch is the part nothing names for you.
		foreach (var entry in ChangeMap
			.Where(e => !e.OldSide && !FixtureAssemblies.IsFixtureSource(e.RelPath))
			.Take(30))
		{
			// The change map's line is wherever the edit landed inside the member, so the
			// member has to come from the enclosing scope; the token at that line is a local
			// or a callee, whose references say nothing about which tests cover the change.
			var member = await sem.GetEnclosingMemberAsync(entry.RelPath, entry.Line, CancellationToken.None);
			if (member is null)
			{
				unresolved++;
				continue;
			}
			traced++;
			// A test rarely names a private helper. When nothing under test refers to the
			// member itself, the type that owns it is what the tests do exercise.
			if (!await AddTestClassesAsync(member) && member is not Microsoft.CodeAnalysis.INamedTypeSymbol
				&& member.ContainingType is { } type)
			{
				await AddTestClassesAsync(type);
			}
			if (classes.Count >= 8)
				break;
		}
		// Which of the ways this can come up empty actually happened, and which half of the
		// answer each name came from: no member resolved, or members resolved but nothing under
		// test refers to them.
		var suggested = fixtures.Concat(classes).Distinct(StringComparer.Ordinal).ToList();
		CliLog.Write("impacted", $"{fixtures.Count} test case(s) named directly; {traced} member(s) traced of "
			+ $"{ChangeMap.Count(e => !e.OldSide && !FixtureAssemblies.IsFixtureSource(e.RelPath))} changed"
			+ (unresolved > 0 ? $" ({unresolved} unresolved)" : "")
			+ $" -> {suggested.Count} test(s)"
			+ (suggested.Count > 0 ? ": " + string.Join(", ", suggested) : ""));
		return suggested;

		async Task<bool> AddTestClassesAsync(Microsoft.CodeAnalysis.ISymbol symbol)
		{
			bool anyTestHit = false;
			foreach (var hit in await sem.FindReferencesAsync(symbol, CancellationToken.None))
			{
				string? rel = sem.ToRelativePath(hit.FilePath);
				if (rel is null || !Core.Review.TestPaths.IsTestPath(rel))
					continue;
				anyTestHit = true;
				classes.Add(Path.GetFileNameWithoutExtension(hit.FilePath));
			}
			return anyTestHit;
		}
	}

	/// <summary>(uncovered, measured) added lines across the diff, from the last coverage run.</summary>
	public (int Uncovered, int Measured) UncoveredAddedLines()
	{
		if (Coverage is not { } coverage)
			return (0, 0);
		int uncovered = 0, measured = 0;
		foreach (var (path, added) in changed.AddedByFile)
		{
			if (!coverage.TryGetValue(path, out var hits))
				continue;
			var (fileUncovered, fileMeasured) = CountUncovered(added, hits);
			uncovered += fileUncovered;
			measured += fileMeasured;
		}
		return (uncovered, measured);
	}

	public void SetCoverage(IReadOnlyDictionary<string, IReadOnlyDictionary<int, int>>? coverage)
	{
		Coverage = coverage;
		CoverageChanged?.Invoke();
	}
	/// <summary>
	/// When the reviewed branch is checked out somewhere with uncommitted work, that
	/// checkout's path. The review's head is then the files on disk rather than a commit,
	/// so head-side text is read from there.
	/// </summary>
	public string? DirtyWorktreePath { get; private set; }

	/// <summary>How many files the head side takes from the working tree rather than a commit.</summary>
	public int UncommittedFileCount { get; private set; }

	public string? WorktreePath { get; private set; }
	public string? BaseWorktreePath { get; private set; }

	/// <summary>
	/// True while the open review was read from the snapshot of its last online pass instead of
	/// from GitHub. Everything git knows is exact - the commits were fetched then and have not
	/// moved - and everything GitHub alone knows is as old as <see cref="OfflineSince"/>.
	/// </summary>
	public bool Offline { get; private set; }

	/// <summary>
	/// The commit the attached pull request is showing, while the review is reading a different
	/// one - a local branch that has moved on from what was pushed. Null whenever the head on
	/// screen is the pull request's own, which is every review opened from the pull request list.
	/// </summary>
	public string? PrHeadSha { get; private set; }

	/// <summary>Whether the code being read is not what the pull request holds. What GitHub
	/// says about lines - a posted comment's line number, a line a new comment could go on -
	/// is about commits it has; here it is about commits it does not.</summary>
	public bool LocalHead => PrHeadSha is not null;

	public DateTimeOffset? OfflineSince { get; private set; }

	/// <summary>What is kept for the next time GitHub cannot be reached, updated as the parts
	/// of it arrive.</summary>
	PrSnapshot? snapshot;

	void KeepSnapshot(PrSnapshot updated)
	{
		snapshot = updated;
		if (!Offline)
			PrCache.Save(Path.GetFileName(RepoPath), updated);
	}

	/// <summary>The comments the snapshot kept, which are the answer while offline.</summary>
	public IReadOnlyList<PostedComment>? SnapshotComments => snapshot?.Comments;

	/// <summary>Keeps what GitHub said about the comments for the next time it cannot be
	/// reached, the same way <see cref="SetChecks"/> keeps the check runs.</summary>
	public void KeepComments(IReadOnlyList<PostedComment> posted)
	{
		if (snapshot is { } current)
			KeepSnapshot(current with { Comments = posted });
	}

	public event Action? ReviewChanged;
	public event Action<string, bool>? ViewedChanged;
	public event Action? SemanticsChanged;
	public event Action? CoverageChanged;
	public event Action? ChecksLoaded;
	public event Action<string, string>? PickaxeRequested;

	public void RequestPickaxe(string text, string path) => PickaxeRequested?.Invoke(text, path);

	/// <summary>Asks the Checks pane (which owns the gh call) to re-fetch CI state;
	/// results arrive through <see cref="ChecksLoaded"/> as usual.</summary>
	public event Action? ChecksRefreshRequested;

	public void RequestChecksRefresh() => ChecksRefreshRequested?.Invoke();
	public event Action<string, IReadOnlyList<ReferenceItem>>? ReferencesAvailable;

	CancellationTokenSource? sessionCts;

	/// <summary>Which lines the change in scope touches, rebuilt whenever <see cref="Files"/> is.</summary>
	ChangedLines changed = ChangedLines.Empty;

	/// <summary>The lines of the change in scope, for whatever asks whether a line is part of
	/// it - the coverage marks, the reference list, the lines a comment can be posted on.</summary>
	public ChangedLines Changed => changed;
	readonly NavigationHistory<NavEntry> history = new();

	/// <summary>
	/// Opens a review of a local base..head range (no PR: checks, posted comments and review
	/// submission stay empty/disabled; everything else works identically).
	///
	/// <paramref name="prNumber"/> attaches the pull request the branch belongs to: its
	/// description, its comment threads, its checks and its reviewers, read against the local
	/// commits rather than the pushed ones. That is the state a branch is in between answering
	/// a review and pushing the answer, and reading the feedback next to the code it is about
	/// should not require pushing first. What GitHub cannot be told about lines it does not
	/// have is refused where it would be posted, not here.
	/// </summary>
	public async Task OpenLocalRangeAsync(string baseRef, string headRef, int? prNumber = null)
	{
		sessionCts?.Cancel();
		var cts = sessionCts = new CancellationTokenSource();
		var ct = cts.Token;

		using var busy = Busy.Begin($"Opening {baseRef}..{headRef}");
		CliLog.Write("action", $"open local range {baseRef}..{headRef}"
			+ (prNumber is { } attached ? $" with PR #{attached}" : ""));
		PrDetail? detail = null;
		string? prHead = null;
		if (prNumber is { } number)
		{
			try
			{
				detail = await GitHub.GetPrAsync(number, ct);
				prHead = await Git.FetchPrHeadAsync(number, ct);
				await Git.FetchBranchAsync(detail.BaseRefName, ct);
				// The pull request's own target, not the repository's default branch: a branch
				// that targets a release branch is not a diff against master.
				baseRef = $"origin/{detail.BaseRefName}";
			}
			catch (ToolFailedException ex)
			{
				// The branch is here either way, and reading it is the point. Losing the
				// discussion is worth a line; failing the whole open over it is not.
				CliLog.Write("gh", $"PR #{number} not attached to this branch review: {ex.Message}");
				detail = null;
				prHead = null;
			}
		}
		string headSha = await ResolveAsync(headRef, ct);
		string baseSha = await Git.GetMergeBaseAsync(await ResolveAsync(baseRef, ct), headSha, ct);
		DirtyWorktreePath = await FindDirtyCheckoutAsync(headRef, ct);
		var committed = await Git.DiffAsync(baseSha, headSha, ct);
		var files = DirtyWorktreePath is { } dirty
			? await Git.DiffWorkingTreeAsync(dirty, baseSha, ct)
			: committed;
		UncommittedFileCount = Math.Max(0, files.Count - committed.Count);
		ct.ThrowIfCancellationRequested();

		Scopes.Reset();
		Reviewers = null;
		CurrentPr = detail;
		Offline = false;
		OfflineSince = null;
		snapshot = null;
		// Only worth saying when the two differ: a branch whose tip is what was pushed reads
		// exactly like the pull request, and a warning about it would be about nothing.
		PrHeadSha = prHead is { } pushed && pushed != headSha ? pushed : null;
		LocalRange = (baseRef, headRef);
		BaseSha = baseSha;
		HeadSha = headSha;
		Files = files;
		changed = ChangedLines.From(files);
		Store.OpenLocal(Path.GetFileName(RepoPath), $"{baseRef}..{headRef}", headSha, baseSha);
		await ApplyReReviewCarryOverAsync(ct);
		await PinReviewHeadsAsync(ct);
		ComputeChurnAsync().HandleExceptions();
		history.Clear();
		CloseDocumentsExceptStart();
		if (detail is null)
			Comments.NoneToLoad();
		ReviewChanged?.Invoke();
		// The overview is where a review starts; files open as the Explorer's list is walked,
		// one tab at a time, instead of arriving as a wall of them.
		OpenOverview();
		CloseStartPage();
		LoadIssueUrlPrefixAsync(ct).HandleExceptions();
		LoadScopeThenSemanticsAsync(headSha, baseSha, ct).HandleExceptions();
		LoadGeneratedSourcesAsync(ct).HandleExceptions();
		Comments.ReattachDraftsAsync(ct).HandleExceptions();
		if (detail is not null && prNumber is { } opened)
		{
			Comments.LoadPostedAsync(opened, ct).HandleExceptions();
			LoadReviewersAsync(opened, ct).HandleExceptions();
		}
	}

	async Task<string> ResolveAsync(string reference, CancellationToken ct)
		=> (await Git.RevParseAsync(reference, ct)).Trim();

	public async Task OpenPrAsync(int number)
	{
		sessionCts?.Cancel();
		var cts = sessionCts = new CancellationTokenSource();
		var ct = cts.Token;

		using var busy = Busy.Begin($"Opening PR #{number}");
		CliLog.Write("action", $"open PR #{number}");
		PrDetail detail;
		string headSha, baseSha;
		Offline = false;
		OfflineSince = null;
		snapshot = null;
		try
		{
			detail = await GitHub.GetPrAsync(number, ct);
			headSha = await Git.FetchPrHeadAsync(number, ct);
			await Git.FetchBranchAsync(detail.BaseRefName, ct);
			baseSha = await Git.GetMergeBaseAsync($"origin/{detail.BaseRefName}", headSha, ct);
		}
		catch (ToolFailedException ex)
		{
			// GitHub, or the network under it, is not there. A pull request read before left a
			// snapshot of what only GitHub knows, and its commits are in the object database
			// from that pass - so the review can be opened exactly as it was then, said so.
			if (await OpenableSnapshotAsync(number, ct) is not { } cached)
				throw;
			CliLog.Write("action", $"PR #{number} opened offline, from the snapshot of {cached.TakenAt:g}: {ex.Message}");
			detail = cached.Detail;
			headSha = cached.HeadSha;
			baseSha = cached.BaseSha;
			Offline = true;
			OfflineSince = cached.TakenAt;
			snapshot = cached;
		}
		DirtyWorktreePath = null;
		UncommittedFileCount = 0;
		var files = await Git.DiffAsync(baseSha, headSha, ct);
		ct.ThrowIfCancellationRequested();

		Scopes.Reset();
		Reviewers = null;
		CurrentPr = detail;
		PrHeadSha = null;
		LocalRange = null;
		BaseSha = baseSha;
		HeadSha = headSha;
		Files = files;
		changed = ChangedLines.From(files);
		Store.Open(Path.GetFileName(RepoPath), number, headSha, baseSha);
		await ApplyReReviewCarryOverAsync(ct);
		await PinReviewHeadsAsync(ct);
		ComputeChurnAsync().HandleExceptions();
		history.Clear();
		CloseDocumentsExceptStart();
		ReviewChanged?.Invoke();
		// The overview is where a review starts; files open as the Explorer's list is walked,
		// one tab at a time, instead of arriving as a wall of them.
		OpenOverview();
		CloseStartPage();
		KeepSnapshot((snapshot ?? new PrSnapshot(detail, headSha, baseSha, DateTimeOffset.Now))
			with { Detail = detail, HeadSha = headSha, BaseSha = baseSha });
		// The snapshot's checks go through the same door the pane's do, so the overview stops
		// waiting for an answer that is not coming. Saying it is offline is the overview's job;
		// a status line as well would say it twice on the page that shows both.
		if (Offline && snapshot?.Checks is { } cachedChecks)
			SetChecks(cachedChecks);
		LoadScopeThenSemanticsAsync(headSha, baseSha, ct).HandleExceptions();
		LoadGeneratedSourcesAsync(ct).HandleExceptions();
		Comments.ReattachDraftsAsync(ct).HandleExceptions();
		Comments.LoadPostedAsync(number, ct).HandleExceptions();
		// Nothing here can be answered from a snapshot, so offline it is left alone rather than
		// asked and failed.
		if (!Offline)
		{
			LoadIssueUrlPrefixAsync(ct).HandleExceptions();
			LoadReviewersAsync(number, ct).HandleExceptions();
		}
	}

	/// <summary>
	/// The snapshot of a pull request, when there is one and the commits it names are still in
	/// the object database. A snapshot whose head has been garbage-collected describes a review
	/// that cannot be built, and reporting the original failure is the honest answer then.
	/// </summary>
	async Task<PrSnapshot?> OpenableSnapshotAsync(int number, CancellationToken ct)
	{
		if (PrCache.Load(Path.GetFileName(RepoPath), number) is not { } cached)
			return null;
		return await Git.TryRevParseAsync(cached.HeadSha, ct) is not null
			&& await Git.TryRevParseAsync(cached.BaseSha, ct) is not null
			? cached
			: null;
	}

	async Task LoadIssueUrlPrefixAsync(CancellationToken ct)
	{
		IssueUrlPrefix = await GitHub.GetIssueUrlPrefixAsync(ct);
		// The description and the comment threads are rendered before this returns.
		ReviewChanged?.Invoke();
		Comments.Rerender();
	}

	/// <summary>
	/// Puts the reader in the scope the last review was left in, then loads the semantic
	/// workspaces onto what that scope shows.
	///
	/// The scope first, because it decides what the review IS: a log and a diff, seconds at
	/// most, while the workspaces take as long as a solution takes to load. Arriving after
	/// them, the restored mode rearranged a review that was already being read - which is a
	/// change of scope, not the scope a review opened in.
	///
	/// Entering a scope overlays the text on screen onto the workspaces, and this load
	/// replaces the workspaces it was put on, so the overlay is applied again at the end.
	/// </summary>
	async Task LoadScopeThenSemanticsAsync(string headSha, string baseSha, CancellationToken ct)
	{
		await Scopes.RestorePreferredAsync(ct);
		await LoadSemanticsAsync(headSha, baseSha, ct);
		if (Scopes.InScope)
			await ApplyScopeSemanticsAsync();
	}

	async Task LoadSemanticsAsync(string headSha, string baseSha, CancellationToken ct)
	{
		Semantics?.Dispose();
		BaseSemantics?.Dispose();
		BaseSemantics = null;
		var semantics = Semantics = new RoslynWorkspaceService();
		semantics.StateChanged += () => SemanticsChanged?.Invoke();
		SemanticsChanged?.Invoke();
		using (Busy.Begin("Loading semantics (head)"))
		{
			WorktreePath = await Worktrees.GetOrCreateAsync(headSha, ct);
			string? chosen = BuildSolutionPreference.For(RepoPath);
			await Task.Run(() => semantics.LoadAsync(WorktreePath, chosen, ct), ct);
		}
		// The base-side workspace powers navigation FROM removed lines. It is the head's own
		// compilation with the review's files reading as they did before, taken from the
		// object database - the two revisions differ in exactly those files, so a second
		// checkout, restore and design-time build would spend minutes to arrive at the same
		// answers.
		var baseSemantics = BaseSemantics = new RoslynWorkspaceService();
		baseSemantics.StateChanged += () => SemanticsChanged?.Invoke();
		using (Busy.Begin("Reading the base side"))
		{
			var (replaced, removed, added) = await BaseSideTextsAsync(baseSha, ct);
			baseSemantics.LoadFrom(semantics, replaced, removed, added);
		}
		using (Busy.Begin("Computing change map"))
			await ComputeChangeMapAsync();
		await PruneCachedWorktreesAsync(ct);
	}

	/// <summary>How many worktrees of a repository outlive the review that made them. Enough
	/// to come back to the last few reviews without checking them out again, few enough that
	/// a year of reading does not fill a disk.</summary>
	const int KeptWorktrees = 6;

	async Task PruneCachedWorktreesAsync(CancellationToken ct)
	{
		try
		{
			var inUse = new List<string>();
			if (HeadSha is not null)
				inUse.Add(HeadSha);
			if (BaseWorktreePath is not null && BaseSha is not null)
				inUse.Add(BaseSha);
			int removed = await Worktrees.PruneToRecentAsync(inUse, KeptWorktrees, ct);
			if (removed > 0)
				CliLog.Write("action", $"pruned {removed} cached worktree(s), kept {KeptWorktrees}");
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolFailedException)
		{
			// Housekeeping: a worktree that will not go away costs disk, not a review.
			CliLog.Write("action", $"worktree cache not pruned: {ex.Message}");
		}
	}

	/// <summary>
	/// What the review's files look like at the base, sorted into what the base-side solution
	/// has to do with each: a modified file reads differently, a file the change adds is not
	/// there at all, and one it deletes is there and gone from the head.
	/// </summary>
	async Task<(Dictionary<string, string> Replaced, List<string> Removed, Dictionary<string, string> Added)>
		BaseSideTextsAsync(string baseSha, CancellationToken ct)
	{
		var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
		var removed = new List<string>();
		var added = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var file in Files.Where(f => !f.IsBinary && f.Generated is null))
		{
			if (file.Kind != FileChangeKind.Added)
			{
				string? text = await Blobs.ReadAsync(baseSha, file.OldPath, ct);
				if (text is not null)
				{
					if (file.Kind == FileChangeKind.Deleted || file.OldPath != file.Path)
						added[file.OldPath] = text;
					else
						replaced[file.OldPath] = text;
				}
			}
			if (file.Kind is FileChangeKind.Added or FileChangeKind.Renamed)
				removed.Add(file.Path);
		}
		return (replaced, removed, added);
	}

	/// <summary>
	/// A checkout of the base revision, made when something asks for one. Building a diff and
	/// navigating it needs no such thing - file content comes from the object database - but
	/// running the tests of both sides, building what their generators emit, or opening the
	/// base in an editor all need a real directory.
	/// </summary>
	public async Task<string?> EnsureBaseWorktreeAsync(CancellationToken ct = default)
	{
		if (BaseWorktreePath is { } existing)
			return existing;
		if (BaseSha is not { } baseSha)
			return null;
		using var busy = Busy.Begin("Checking out the base");
		BaseWorktreePath = await Worktrees.GetOrCreateAsync(baseSha, ct);
		return BaseWorktreePath;
	}

	/// <summary>Progress of the generated-source pass, for the preparation checklist.</summary>
	public string GeneratedSourcesStatus { get; private set; } = "waiting";
	public bool GeneratedSourcesDone { get; private set; }
	public event Action? GeneratedSourcesChanged;

	/// <summary>
	/// Adds what the builds generated to the reviewed files, replacing whatever an earlier
	/// pass put there. They sort after the committed files: they are the consequence of the
	/// change, and nobody should have to scroll past a generator's output to reach the code
	/// that caused it.
	/// </summary>
	public void SetGeneratedFiles(IReadOnlyList<FileDiff> generated)
	{
		Files = [.. Files.Where(f => !f.IsGenerated), .. generated];
		ReviewChanged?.Invoke();
	}

	/// <summary>
	/// Builds both sides so the review can show what source generators emitted, which is
	/// otherwise invisible: generated code is not in git, so a change that is entirely about
	/// what a generator produces shows up as an edit to the generator and nothing else.
	///
	/// The head is built first and, when it generated nothing, the base is not built at all -
	/// a repository without generators pays one build instead of two, and learns the answer
	/// the only way there is to learn it.
	/// </summary>
	async Task LoadGeneratedSourcesAsync(CancellationToken ct)
	{
		try
		{
			// The semantic load is a design-time build over these same trees; running a real
			// build alongside it has both writing the same obj directories. The A/B run waits
			// for the same reason.
			//
			// It is also what creates the head worktree, and waiting covers both: this used to
			// read WorktreePath before that had happened - the two are started one after the
			// other - and gave up with "no worktrees" every time a review was opened. Only a
			// second pass over the same review found the field filled in, which is why
			// generated sources appeared to be a thing that commit-by-commit mode had.
			SetGeneratedStatus("waiting for semantics...", done: false);
			while (Semantics is null or { State: SemanticState.NotLoaded or SemanticState.Restoring or SemanticState.Loading }
				|| BaseSemantics is { State: SemanticState.Restoring or SemanticState.Loading })
			{
				await Task.Delay(1000, ct);
			}
			if (WorktreePath is not { } head || await EnsureBaseWorktreeAsync(ct) is not { } baseTree)
			{
				SetGeneratedStatus("no worktrees", done: true);
				return;
			}
			SetGeneratedStatus("building head...", done: false);
			string? solution = BuildSolutionPreference.For(RepoPath);
			await GeneratedSources.BuildAsync(head, solution, ct);
			if (GeneratedSources.Collect(head).Count == 0)
			{
				SetGeneratedStatus("no generators", done: true);
				return;
			}
			SetGeneratedStatus("building base...", done: false);
			await GeneratedSources.BuildAsync(baseTree, solution, ct);
			var generated = await GeneratedSources.DiffAsync(baseTree, head, ct);
			SetGeneratedFiles(generated);
			SetGeneratedStatus(generated.Count == 0 ? "unchanged" : $"{generated.Count} file(s)", done: true);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (ToolFailedException ex)
		{
			// A review of a change that does not build is still a review; this is the one
			// part of the preparation whose failure is expected often enough to be normal.
			CliLog.Write("dotnet", $"generated sources unavailable: {ex.Message}");
			SetGeneratedStatus("build failed", done: true);
		}
	}

	void SetGeneratedStatus(string status, bool done)
	{
		GeneratedSourcesStatus = status;
		GeneratedSourcesDone = done;
		GeneratedSourcesChanged?.Invoke();
	}

	/// <summary>
	/// Opens a file's change in the layout that is set, one document per file either way. The
	/// two layouts are two views of the same thing, so a file that is already open in the other
	/// one is rebuilt rather than joined by a second tab.
	/// </summary>
	public async Task<Documents.IDiffDocument?> OpenFileAsync(FileDiff file)
	{
		if (file.Generated is { } generated)
			return ShowDiffDocument(file, ReadOrEmpty(generated.BaseFile), ReadOrEmpty(generated.HeadFile));
		if (BaseSha is null || HeadSha is null)
			return null;
		string oldText = file.Kind == FileChangeKind.Added || file.IsBinary
			? ""
			: await Blobs.ReadAsync(BaseSha, file.OldPath) ?? "";
		string newText = file.Kind == FileChangeKind.Deleted || file.IsBinary
			? ""
			: await ReadHeadFileAsync(file.NewPath);
		return ShowDiffDocument(file, oldText, newText);

		static string ReadOrEmpty(string? path) => path is null ? "" : File.ReadAllText(path);
	}

	/// <summary>
	/// The document for a file, built in the layout that is set. Both layouts key on the same
	/// id: it is the same file being read, so it is one tab, one entry in history, and one
	/// thing to reopen after a rebuild. A document left over from the other layout is closed
	/// as this one takes its place.
	/// </summary>
	Documents.IDiffDocument? ShowDiffDocument(FileDiff file, string oldText, string newText)
	{
		if (Documents is null || Factory is null)
			return null;
		string id = "diff:" + file.Path;
		string title = Path.GetFileName(file.Path);
		bool sideBySide = DiffLayoutPreference.SideBySide;
		var existing = Documents.VisibleDockables?.FirstOrDefault(d => d.Id == id);
		if (existing is Documents.IDiffDocument open && open is SideBySideDocumentViewModel == sideBySide)
		{
			Factory.SetActiveDockable((Dock.Model.Core.IDockable)open);
			Factory.SetFocusedDockable(Documents, (Dock.Model.Core.IDockable)open);
			return open;
		}
		if (existing is not null)
			Factory.CloseDockable(existing);
		Dock.Model.Mvvm.Controls.Document created = sideBySide
			? new SideBySideDocumentViewModel(file, DiffDocumentBuilder.BuildPair(oldText, newText)) { Title = title }
			: new DiffDocumentViewModel(file, DiffDocumentBuilder.Build(oldText, newText)) { Title = title };
		created.Id = id;
		Factory.AddDockable(Documents, created);
		Factory.SetActiveDockable(created);
		Factory.SetFocusedDockable(Documents, created);
		return (Documents.IDiffDocument)created;
	}

	/// <summary>
	/// Loads the semantics and the generated sources again, for when what they were built from
	/// has changed. The compilation is what every semantic answer comes from, so a solution
	/// named after one was loaded is a choice that means nothing until this runs.
	/// </summary>
	public void ReloadSemantics()
	{
		if (HeadSha is not { } headSha || BaseSha is not { } baseSha || sessionCts is not { } cts)
			return;
		PostStatus("Reloading semantics for the solution that was chosen...");
		LoadSemanticsAsync(headSha, baseSha, cts.Token).HandleExceptions();
		LoadGeneratedSourcesAsync(cts.Token).HandleExceptions();
	}

	/// <summary>Rebuilds every open file in the layout that is now set, keeping the reader in
	/// front of the tab they were in.</summary>
	public async Task ApplyDiffLayoutAsync()
	{
		if (Documents?.VisibleDockables is null)
			return;
		string? active = Documents.ActiveDockable?.Id;
		var paths = Documents.VisibleDockables
			.Select(d => d.Id)
			.OfType<string>()
			.Where(id => id.StartsWith("diff:", StringComparison.Ordinal))
			.Select(id => id["diff:".Length..])
			.ToList();
		foreach (string path in paths)
		{
			if (Files.FirstOrDefault(f => f.Path == path) is { } file)
				await OpenFileAsync(file);
		}
		if (active is not null && Documents.VisibleDockables.FirstOrDefault(d => d.Id == active) is { } front
			&& Factory is not null)
		{
			Factory.SetActiveDockable(front);
		}
	}

	T? ShowDocument<T>(string id, Func<T> create) where T : Dock.Model.Mvvm.Controls.Document
	{
		if (Documents is null || Factory is null)
			return null;
		var existing = Documents.VisibleDockables?
			.OfType<T>()
			.FirstOrDefault(d => d.Id == id);
		if (existing is null)
		{
			existing = create();
			existing.Id = id;
			Factory.AddDockable(Documents, existing);
		}
		Factory.SetActiveDockable(existing);
		Factory.SetFocusedDockable(Documents, existing);
		return existing;
	}

	/// <summary>The file being read, in whichever layout it is being read in. Asked of the
	/// document interface rather than of the unified type: everything that acts on "the file in
	/// front" - marking it viewed, commenting, stepping to the next one - went dead in the
	/// side-by-side layout while this answered null there.</summary>
	public FileDiff? CurrentFile => (Documents?.ActiveDockable as Documents.IDiffDocument)?.File;

	/// <summary>Whether the tests of a change are read before the code they are about. A
	/// review's own setting rather than the file list's: it decides the order the change is
	/// read in, and every key that moves between files follows that order.</summary>
	public bool TestsFirst { get; set; } = true;

	/// <summary>
	/// The files in the order they are meant to be read - the order the changed-files list
	/// shows them in, which is the order a reader is walking down.
	///
	/// Navigation used the order git printed the diff in, which is neither: "the next file"
	/// jumped somewhere else in the list, and "the last file" - where a pass ends and the
	/// verdict page opens - was a file in the middle of it, so the end of the pass never
	/// arrived. One order, asked for here, is what keeps the two in step.
	/// </summary>
	public IReadOnlyList<FileDiff> ReadingOrder => [.. Files
		// Generator output goes last whatever else is true of it: it is what the change
		// caused, and reaching the cause should never mean scrolling past the effect.
		.OrderBy(f => f.IsGenerated ? 1 : 0)
		// Every file in the since-last-pass scope changed since the last pass - that is what
		// the list is - so ordering by it there orders nothing.
		.ThenBy(f => !Scopes.InSinceLastPass && IsTouchedSinceLastPass(f.Path) ? 0 : 1)
		.ThenBy(f => Core.Review.TestPaths.IsTestPath(f.Path) == TestsFirst ? 0 : 1)
		.ThenBy(f => f.Path, StringComparer.Ordinal)];

	public event Action<string>? StatusMessage;

	/// <summary>Outcome line of the most recent local test run, for the overview.</summary>
	public string? LastTestSummary { get; private set; }

	public event Action? TestResultsChanged;

	public void SetTestSummary(string summary)
	{
		LastTestSummary = summary;
		TestResultsChanged?.Invoke();
	}

	public void PostStatus(string message) => StatusMessage?.Invoke(message);

	#region Re-review (head moved since the last pass)

	/// <summary>Head of the previous pass: what the reader compared against last time. Null
	/// on a first pass, or when that head's objects are no longer in the repository.</summary>
	public string? LastPassHead { get; private set; }

	/// <summary>The base that head was read against, so the work of that pass is identified
	/// as a range and not just a tip.</summary>
	public string? LastPassBase { get; private set; }

	/// <summary>Files the interdiff (last pass head -> current head) touched.</summary>
	public IReadOnlySet<string>? TouchedSinceLastPass { get; private set; }

	public bool IsTouchedSinceLastPass(string path) => TouchedSinceLastPass?.Contains(path) ?? false;

	/// <summary>Re-review is not a repeat: viewed flags carry over except for files the new
	/// push touched, so the unviewed set - which drives which files open - becomes exactly
	/// "invalidated plus never seen".</summary>
	async Task ApplyReReviewCarryOverAsync(CancellationToken ct)
	{
		LastPassHead = null;
		LastPassBase = null;
		TouchedSinceLastPass = null;
		Scopes.ForgetSinceLastPassTree();
		if (HeadSha is null)
			return;
		// The baseline comes from the store rather than from the move this open discovered:
		// a reader who closes the app between two passes still wants the diff against what
		// they read last time.
		if (Store.PreviousHead is { } previous)
		{
			if (await Git.HasCommitAsync(previous, ct))
			{
				LastPassHead = previous;
				LastPassBase = Store.PreviousBase;
			}
			else
			{
				StatusMessage?.Invoke($"The head you read last time ({previous[..9]}) is no longer in this "
					+ "repository, so there is nothing to compare this pass against.");
			}
		}
		if (Store.Superseded is not { } superseded)
			return;
		try
		{
			var changes = await Git.DiffNameStatusAsync(superseded.PreviousHead, HeadSha, ct);
			var touched = changes.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
			TouchedSinceLastPass = touched;
			var carried = ReReview.CarryOverViewed(superseded.PreviousViewed, touched);
			foreach (var path in carried)
				Store.SetViewed(path, true);
			int invalidated = superseded.PreviousViewed.Count(kv => kv.Value) - carried.Count;
			CliLog.Write("action", $"re-review: {superseded.PreviousHead[..9]} -> {HeadSha[..9]}, {touched.Count} touched, {carried.Count} carried, {invalidated} invalidated");
			StatusMessage?.Invoke(
				$"Re-review: head moved {superseded.PreviousHead[..9]} -> {HeadSha[..9]}. " +
				$"{touched.Count} file(s) touched since your last pass; {carried.Count} viewed flag(s) carried over, {invalidated} invalidated.");
		}
		catch (ToolFailedException)
		{
			// The previous head's objects are no longer reachable (pruned after a
			// force-push): fall back to a full re-pass rather than guessing.
			StatusMessage?.Invoke("Re-review: previous head is gone from the object store; viewed flags were reset.");
		}
	}

	/// <summary>The ref-name component this review's pinned commits live under. A pull
	/// request is named by its number; a local range by its text, with everything a ref name
	/// cannot carry replaced.</summary>
	string ReviewRefKey()
		=> CurrentPr is { } pr
			? $"pr/{pr.Number}"
			: "local/" + new string((LocalRange is { } range ? $"{range.Base}..{range.Head}" : HeadSha ?? "")
				.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());

	async Task PinReviewHeadsAsync(CancellationToken ct)
	{
		if (HeadSha is not { } head)
			return;
		try
		{
			await Git.PinReviewHeadsAsync(ReviewRefKey(), head, LastPassHead, ct);
		}
		catch (ToolFailedException ex)
		{
			// Losing the pin costs the next pass its comparison point, not this one its review.
			StatusMessage?.Invoke($"Could not pin the reviewed head {head[..9]}: {ex.Message} "
				+ "A later force-push may leave nothing to compare against.");
		}
	}

	/// <summary>The raw interdiff (last pass head -> current head) as a document: what the
	/// scope shows when the reviewed work cannot be replayed onto the current base, and the
	/// commits the rebase brought in cannot be told from the author's own.</summary>
	public async Task OpenInterdiffAsync()
	{
		if (LastPassHead is null || HeadSha is null)
		{
			StatusMessage?.Invoke("No earlier pass is recorded for this review - Stampeded compares against the head you last opened it at, and this is the first.");
			return;
		}
		try
		{
			string patch = await Git.DiffPatchAsync(LastPassHead, HeadSha);
			OpenPatchDocument($"interdiff:{LastPassHead[..9]}", $"interdiff {LastPassHead[..9]}..{HeadSha[..9]}", patch, HeadSha);
		}
		catch (ToolFailedException ex)
		{
			StatusMessage?.Invoke($"Interdiff failed: {ex.Message}");
		}
	}

	#endregion

	#region Review phases: triage

	public Core.Review.TriageTotals ComputeTriage() => Core.Review.TriageEstimate.Compute(Files);

	/// <summary>Commits per file over the last year - churn correlates with defect density,
	/// so a change in a hot spot deserves more triage caution. Computed once per repo.</summary>
	public IReadOnlyDictionary<string, int>? ChurnByFile { get; private set; }

	public event Action? ChurnChanged;

	async Task ComputeChurnAsync()
	{
		if (ChurnByFile is not null)
			return;
		try
		{
			ChurnByFile = await Git.GetChurnAsync("1.year");
			ChurnChanged?.Invoke();
		}
		catch (ToolFailedException)
		{
			// Shallow or odd repos: triage simply shows no churn column.
		}
	}

	static string MemberSimpleName(string display)
	{
		int paren = display.IndexOf('(');
		string noArgs = paren < 0 ? display : display[..paren];
		int dot = noArgs.LastIndexOf('.');
		return dot < 0 ? noArgs : noArgs[(dot + 1)..];
	}

	#endregion

	/// <summary>Opens the side-by-side view of the active file (or a given one).</summary>
	/// <summary>Switches the layout every file is read in, and rebuilds what is open in it.
	/// A file is one document either way, so this replaces the tabs rather than adding to
	/// them.</summary>
	public async Task SetDiffLayoutAsync(bool sideBySide)
	{
		if (DiffLayoutPreference.SideBySide == sideBySide)
			return;
		DiffLayoutPreference.Set(sideBySide);
		CliLog.Write("action", $"diff layout: {(sideBySide ? "side-by-side" : "unified")}");
		await ApplyDiffLayoutAsync();
	}

	/// <summary>Removes cached worktrees except the current review's base and head.</summary>
	public async Task PruneWorktreeCacheAsync()
	{
		// The review's own base and head: inside a scope those are not what BaseSha and
		// HeadSha say, and keeping the scope's instead would delete the worktrees the
		// semantic workspaces are loaded from.
		var keep = new List<string>();
		if (Scopes.ReviewRange is { } range)
			keep.AddRange([range.Base, range.Head]);
		int removed = await Worktrees.PruneAsync(keep);
		StatusMessage?.Invoke($"Pruned {removed} cached worktree(s).");
	}

	/// <summary>Opens one commit's change to one file as a read-only historical diff tab.</summary>
	public async Task OpenHistoricalDiffAsync(string sha, string path)
	{
		string oldText = "";
		string newText = "";
		try
		{
			newText = await Git.ShowFileAsync(sha, path);
		}
		catch (ToolFailedException)
		{
			// Deleted in this commit, or the path is unknown at it (rename): fall back to
			// the whole commit as text.
			try
			{
				string patch = await Git.ShowCommitAsync(sha);
				OpenPatchDocument($"show:{sha}", $"commit {sha[..9]}", patch, sha);
			}
			catch (ToolFailedException)
			{
			}
			return;
		}
		try
		{
			oldText = await Git.ShowFileAsync($"{sha}^", path);
		}
		catch (ToolFailedException)
		{
			// Added in this commit (or rename): empty old side shows it as all-new.
		}
		var stub = new FileDiff(path, path, FileChangeKind.Modified, false, []);
		var vm = ShowDocument($"hist:{sha}:{path}", () =>
			new DiffDocumentViewModel(stub, DiffDocumentBuilder.Build(oldText, newText)) {
				Title = $"{Path.GetFileName(path)} @ {sha[..9]}",
				Historical = true,
				HistoricalSha = sha,
			});
		CliLog.Write("action", $"historical diff {sha[..9]} {path}");
	}

	/// <summary>Rebases a PR onto its target branch (server-side) without opening a
	/// review, for acting on a PR from the start page. Rewrites the PR branch - explicit
	/// user action only.</summary>
	public async Task RebasePrAsync(int number)
	{
		using var busy = Busy.Begin($"Rebasing #{number}");
		try
		{
			await GitHub.UpdateBranchAsync(number);
			StatusMessage?.Invoke($"#{number} rebased onto its target branch.");
		}
		catch (ToolFailedException ex)
		{
			StatusMessage?.Invoke($"Rebase of #{number} failed: {ex.Message}");
		}
	}

	/// <summary>Rebases the current PR onto its target branch (server-side), then reopens
	/// the review on the new head. Rewrites the PR branch - explicit user action only.</summary>
	public async Task RebaseCurrentPrOnTargetAsync()
	{
		if (CurrentPr is not { } pr)
			return;
		using var busy = Busy.Begin($"Rebasing #{pr.Number} onto {pr.BaseRefName}");
		try
		{
			await GitHub.UpdateBranchAsync(pr.Number);
			StatusMessage?.Invoke($"#{pr.Number} rebased onto {pr.BaseRefName}; reloading the review...");
			// The API is asynchronous server-side; give the new head a moment to exist.
			await Task.Delay(TimeSpan.FromSeconds(3));
			await OpenPrAsync(pr.Number);
		}
		catch (ToolFailedException ex)
		{
			StatusMessage?.Invoke($"Rebase failed: {ex.Message}");
		}
	}

	/// <summary>The checkout holding this branch, if it has uncommitted work. Reviewing a
	/// branch you are still editing should show what is on disk, not the last commit.</summary>
	async Task<string?> FindDirtyCheckoutAsync(string headRef, CancellationToken ct)
	{
		try
		{
			foreach (var checkout in await Git.ListWorktreesAsync(ct))
			{
				if (checkout.Branch == headRef && await Git.IsDirtyAsync(checkout.Path, ct))
					return checkout.Path;
			}
		}
		catch (ToolFailedException)
		{
			// Worktree discovery is an enhancement; a review of the commit still works.
		}
		return null;
	}

	/// <summary>
	/// Points the semantic workspaces at the revision being displayed. They stay loaded
	/// for the review's head - reloading one per commit would mean a checkout and a
	/// solution load each step - so the files this commit touches are overlaid instead,
	/// which is what makes positions, symbols and occurrences agree with the text shown.
	/// </summary>
	internal async Task ApplyScopeSemanticsAsync()
	{
		if (BaseSha is not { } origin || HeadSha is not { } displayed)
			return;
		var headText = new Dictionary<string, string>(StringComparer.Ordinal);
		var originText = new Dictionary<string, string>(StringComparer.Ordinal);
		try
		{
			// Through the one reader rather than a process per file: stepping through a series
			// reads every changed file of every commit twice, and a commit touching fifty
			// files would otherwise spend a hundred processes on it.
			foreach (var file in Files.Where(f => !f.IsBinary))
			{
				if (file.Kind != FileChangeKind.Deleted
					&& await Blobs.ReadAsync(displayed, file.NewPath) is { } head)
				{
					headText[file.NewPath] = head;
				}
				if (file.Kind != FileChangeKind.Added
					&& await Blobs.ReadAsync(origin, file.OldPath) is { } before)
				{
					originText[file.OldPath] = before;
				}
			}
		}
		catch (ToolFailedException ex)
		{
			// Without the overlay the workspaces still answer, but about the review's head
			// rather than what is on screen - which is worth saying rather than leaving the
			// reader to wonder why a symbol resolves oddly.
			StatusMessage?.Invoke($"Semantics for this scope are the review's, not the scope's: {ex.Message}");
			return;
		}
		Semantics?.SetTextOverlay(headText);
		BaseSemantics?.SetTextOverlay(originText);
		SemanticsChanged?.Invoke();
	}

	/// <summary>Back to answering about the review's own head, on the way out of a scope.</summary>
	internal void ClearScopeSemantics()
	{
		Semantics?.ClearTextOverlay();
		BaseSemantics?.ClearTextOverlay();
	}

	/// <summary>
	/// Points the review at the range a scope is showing. Only <see cref="Scopes"/> calls this:
	/// which range is on screen is its decision, and the four things that describe one have to
	/// move together or the diff, its line index and the state file stop agreeing.
	/// </summary>
	internal void SetScopeContent(string baseSha, string headSha, IReadOnlyList<FileDiff> files)
	{
		BaseSha = baseSha;
		HeadSha = headSha;
		Files = files;
		changed = ChangedLines.From(files);
	}

	/// <summary>
	/// Rebuilds the window around a scope that has just changed what is being read: the change
	/// map goes (it described the last one), the open tabs are reopened on the new content, and
	/// the reader lands back in front of the tab they were in. Entering a scope, stepping
	/// through the series and leaving again all end here, because all three replace the diff
	/// under the same tabs.
	/// </summary>
	internal async Task RebuildForScopeAsync(string logLine)
	{
		ResetChangeMap();
		var open = CaptureOpenDocuments();
		CloseDocumentsExceptStart();
		CliLog.Write("action", logLine);
		ReviewChanged?.Invoke();
		OpenOverview();
		await ReopenDocumentsAsync(open);
		ComputeChangeMapAsync().HandleExceptions();
	}

	/// <summary>Stops background work and releases the Roslyn workspaces; called when the
	/// app switches to another repository and this instance is abandoned.</summary>
	public void Shutdown()
	{
		sessionCts?.Cancel();
		Blobs.Dispose();
		Semantics?.Dispose();
		BaseSemantics?.Dispose();
	}

	/// <summary>Head-side text of a file, or null when the head does not have it.</summary>
	async Task<string?> ReadFileAtHeadAsync(string relPath, CancellationToken ct = default)
	{
		if (DirtyWorktreePath is { } dir)
		{
			string absolute = Path.Combine(dir, relPath);
			if (File.Exists(absolute))
				return await File.ReadAllTextAsync(absolute, ct);
		}
		return HeadSha is { } head ? await Blobs.ReadAsync(head, relPath, ct) : null;
	}

	/// <summary>Head-side text of a file: read from the checkout under review when that is
	/// what the head means, else from the commit.</summary>
	public Task<string> ReadHeadFileAsync(string relPath, CancellationToken ct = default)
	{
		if (DirtyWorktreePath is { } dir)
		{
			string absolute = Path.Combine(dir, relPath);
			if (File.Exists(absolute))
				return File.ReadAllTextAsync(absolute, ct);
		}
		return Git.ShowFileAsync(HeadSha!, relPath, ct);
	}

	/// <summary>
	/// Opens a URL in whatever the desktop uses for it. Each platform has its own opener and
	/// naming only xdg-open meant every link in the app - the pull request, a comment, a
	/// commit - did nothing at all off Linux.
	/// </summary>
	public Task OpenUrlAsync(string url)
	{
		// "start" is a cmd builtin, not a program, and its first quoted argument is the
		// window title - hence the empty one before the URL.
		var (tool, args) = OperatingSystem.IsWindows() ? ("cmd", (string[])["/c", "start", "", url])
			: OperatingSystem.IsMacOS() ? ("open", [url])
			: ("xdg-open", [url]);
		return ExternalTool.RunAsync(tool, args, RepoPath);
	}

	/// <summary>Opens a commit on GitHub via gh.</summary>
	public Task OpenCommitOnGitHubAsync(string sha)
		=> ExternalTool.RunAsync("gh", ["browse", sha], RepoPath);

	/// <summary>Opens a PR in the browser via gh.</summary>
	public Task OpenOnGitHubAsync(int number)
		=> ExternalTool.RunAsync("gh", ["pr", "view", number.ToString(), "--web"], RepoPath);

	/// <summary>Opens the head (or base) worktree in VS Code, optionally at a file:line,
	/// for full IDE debugging of the reviewed revision. The source clone's .vscode is
	/// linked into the worktree so the user's own launch configs work there.</summary>
	public async Task OpenInVsCodeAsync(bool oldSide, string? relPath = null, int? line = null)
	{
		string? root = oldSide ? await EnsureBaseWorktreeAsync() : WorktreePath;
		if (root is null)
		{
			StatusMessage?.Invoke("No worktree yet - open a review first.");
			return;
		}
		LinkVsCodeConfig(root);
		List<string> args = [root];
		if (relPath is not null && line is not null)
			args.AddRange(["--goto", $"{Path.Combine(root, relPath)}:{line}"]);
		await ExternalTool.RunAsync("code", args, root);
		StatusMessage?.Invoke($"VS Code opened on the {(oldSide ? "base" : "head")} worktree.");
	}

	void LinkVsCodeConfig(string worktreeDir)
	{
		string source = Path.Combine(RepoPath, ".vscode");
		string target = Path.Combine(worktreeDir, ".vscode");
		if (!Directory.Exists(source) || Directory.Exists(target) || File.Exists(target))
			return;
		try
		{
			Directory.CreateSymbolicLink(target, source);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			CliLog.Write("vscode", $"could not link .vscode: {ex.Message}");
		}
	}

	/// <summary>True for repos with ILSpy's decompiler test-case layout; gates the
	/// fixtures-in-ILSpy action.</summary>
	public bool HasDecompilerTestCases
		=> Directory.Exists(Path.Combine(RepoPath, "ICSharpCode.Decompiler.Tests", "TestCases"));

	/// <summary>ILSpy-specific: opens the compiled fixture assemblies of every test case
	/// this change touches in the ILSpy UI built from the review head, so the reviewer can
	/// inspect the new decompilation interactively. Fixture assemblies are compiled next
	/// to their sources by the decompiler test suite, so a test run must have happened in
	/// the head worktree first.</summary>
	public async Task OpenAffectedFixturesInILSpyAsync()
	{
		if (WorktreePath is not { } root)
		{
			StatusMessage?.Invoke("No head worktree yet - open a review first.");
			return;
		}
		var fixtures = FixtureAssemblies.AffectedFixtures(Files.Select(f => f.Path));
		var assemblies = new List<string>();
		foreach (var (relDir, name) in fixtures)
		{
			string dir = Path.Combine(root, relDir);
			if (!Directory.Exists(dir))
				continue;
			assemblies.AddRange(Directory.EnumerateFiles(dir)
				.Where(f => FixtureAssemblies.IsAssemblyOf(name, Path.GetFileName(f)))
				.Order());
		}
		if (fixtures.Count == 0)
		{
			StatusMessage?.Invoke("This change touches no decompiler test cases.");
			return;
		}
		if (assemblies.Count == 0)
		{
			StatusMessage?.Invoke(
				"No compiled fixture assemblies found - run the decompiler tests first; they compile fixtures next to their sources.");
			return;
		}
		string apphost = Path.Combine(root, "ILSpy", "bin", "Debug", "net10.0",
			OperatingSystem.IsWindows() ? "ILSpy.exe" : "ILSpy");
		if (!File.Exists(apphost))
		{
			StatusMessage?.Invoke("Building ILSpy from the review head (first time only)...");
			try
			{
				// Pruning stays off, matching ILSpy's restore.ps1: its core libraries
				// restore in locked mode and fail against a pruned package graph.
				await ExternalTool.RunAsync("dotnet",
					["build", "ILSpy/ILSpy.csproj", "-p:RestoreEnablePackagePruning=false"],
					root, env: new Dictionary<string, string> { ["OPENSSL_ENABLE_SHA1_SIGNATURES"] = "1" });
			}
			catch (ToolFailedException ex)
			{
				StatusMessage?.Invoke($"ILSpy build failed: {ex.Message}");
				return;
			}
		}
		var psi = new System.Diagnostics.ProcessStartInfo(apphost) {
			WorkingDirectory = root,
			UseShellExecute = false,
		};
		psi.Environment["OPENSSL_ENABLE_SHA1_SIGNATURES"] = "1";
		foreach (var assembly in assemblies)
			psi.ArgumentList.Add(assembly);
		System.Diagnostics.Process.Start(psi);
		StatusMessage?.Invoke(
			$"Opened {assemblies.Count} assembly(ies) of {fixtures.Count} affected fixture(s) in the head-built ILSpy.");
	}

	/// <summary>The start page document; owns the preparation overlay state. One instance
	/// for the workspace's lifetime - it is closed while a review is open and re-added on
	/// Close Review, keeping its subscriptions and the overlay binding intact.</summary>
	public Documents.StartDocumentViewModel? StartPage { get; private set; }

	public void OpenStart()
	{
		StartPage ??= new Documents.StartDocumentViewModel(this);
		ShowDocument("start", () => StartPage);
	}

	/// <summary>Ends the review session: background work cancelled, semantics released,
	/// state cleared, every review document closed and the start page back in front.</summary>
	/// <summary>
	/// Ends the review, asking first when drafts were never submitted. They are not lost by
	/// closing - the review's state keeps them, and they are there again when it is reopened
	/// - but someone who meant to send them would rather hear it now than discover it later.
	/// </summary>
	public async Task CloseReviewAsync()
	{
		var drafts = Comments.Drafts;
		if (drafts.Count > 0 && MainWindowOrNull() is { } owner)
		{
			int outdated = drafts.Count(d => d.CurrentLine is null);
			bool close = await new ConfirmWindow("Close review",
				$"{drafts.Count} draft comment(s) have not been submitted"
					+ (outdated > 0 ? $" ({outdated} outdated)" : "") + ".\n\n"
					+ "Closing keeps them: they are stored with the review and will be here when you open it again.",
				"Close review").ShowDialog<bool>(owner);
			if (!close)
			{
				PostStatus($"Review left open; {drafts.Count} draft(s) still unsubmitted.");
				return;
			}
		}
		CloseReview();
	}

	/// <summary>
	/// Reads the review again at whatever its head is now. A review is a snapshot: it is taken
	/// when it is opened and nothing behind it moves it, so a push during the read left the
	/// only way to see it as closing the review and opening it again.
	///
	/// It goes through the same open the start page does, at the same PR number or the same
	/// pair of refs, which is what makes it a reload rather than a second thing to keep
	/// correct. Nothing recorded is lost: drafts and viewed flags live with the review, and a
	/// head that moved lands in the re-review carry-over, which keeps the flags of the files
	/// the new head did not touch and says what it invalidated.
	/// </summary>
	public async Task ReloadReviewAsync()
	{
		string? before = HeadSha;
		var open = CaptureOpenDocuments();
		// The range first: a branch review with a pull request attached has both, and reloading
		// it as the pull request would quietly replace the local commits with the pushed ones.
		if (LocalRange is { } local)
			await OpenLocalRangeAsync(local.Base, local.Head, CurrentPr?.Number);
		else if (CurrentPr is { } pr)
			await OpenPrAsync(pr.Number);
		else
			return;
		// A head that moved is reported by the carry-over, which knows what it kept; standing
		// still is the outcome nothing else would mention.
		await ReopenDocumentsAsync(open);
		if (HeadSha == before)
			PostStatus($"Reloaded: the head is still {before?[..9]}.");
	}

	static Avalonia.Controls.Window? MainWindowOrNull()
		=> (Avalonia.Application.Current?.ApplicationLifetime
			as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

	public void CloseReview()
	{
		sessionCts?.Cancel();
		Semantics?.Dispose();
		Semantics = null;
		BaseSemantics?.Dispose();
		BaseSemantics = null;
		CurrentPr = null;
		PrHeadSha = null;
		LocalRange = null;
		Scopes.Reset();
		DirtyWorktreePath = null;
		UncommittedFileCount = 0;
		BaseSha = null;
		HeadSha = null;
		Files = [];
		changed = ChangedLines.Empty;
		Coverage = null;
		Checks = null;
		Comments.Clear();
		LastPassHead = null;
		TouchedSinceLastPass = null;
		ResetChangeMap();
		history.Clear();
		CloseDocumentsExceptStart();
		OpenStart();
		ReviewChanged?.Invoke();
		SemanticsChanged?.Invoke();
		CoverageChanged?.Invoke();
		ChecksLoaded?.Invoke();
		CliLog.Write("action", "review closed");
	}

	/// <summary>What was open before a scope switch or a reload rebuilt the review, so it can
	/// be put back.</summary>
	readonly record struct OpenDocuments(IReadOnlyList<string> Ids, string? Active);

	OpenDocuments CaptureOpenDocuments()
		=> new(
			[.. Documents?.VisibleDockables?.Select(d => d.Id).OfType<string>() ?? []],
			Documents?.ActiveDockable?.Id);

	/// <summary>
	/// Opens again what a rebuild closed, in the order it was open, and puts the reader back in
	/// front of the tab they were in. Switching to commit-by-commit or reloading after a push
	/// keeps the files, not only the review: the alternative is finding the way back to each
	/// one through the Explorer, which is the work the mode was entered to avoid.
	///
	/// A file the new view does not contain stays closed - the commit being read did not touch
	/// it, so there is no diff of it to show. Historical tabs - one commit's change, a patch, an
	/// interdiff - are not reopened either: they are snapshots of something other than the
	/// review, and nothing in the rebuilt review says what they held.
	/// </summary>
	async Task ReopenDocumentsAsync(OpenDocuments open)
	{
		foreach (string id in open.Ids)
		{
			if (id == "review")
			{
				OpenReviewDocument();
				continue;
			}
			// "sbs:" ids were written by builds where side-by-side was a second document per
			// file; they name the same file and reopen as the one document it now has.
			if (!id.StartsWith("diff:", StringComparison.Ordinal) && !id.StartsWith("sbs:", StringComparison.Ordinal))
				continue;
			if (Files.FirstOrDefault(f => f.Path == id[(id.IndexOf(':') + 1)..]) is not { } file)
				continue;
			await OpenFileAsync(file);
		}
		// Every reopen activates what it opened, so without this the front tab is whichever
		// came last - an arbitrary file. The reader goes back to the tab they were in, or to
		// the overview when the new view has no diff of it: that one says what is now being
		// read, where an unrelated file only looks like the rebuild lost its place.
		if (Factory is null || Documents?.VisibleDockables is not { } tabs)
			return;
		var front = tabs.FirstOrDefault(d => d.Id == open.Active) ?? tabs.FirstOrDefault(d => d.Id == "overview");
		if (front is not null)
		{
			Factory.SetActiveDockable(front);
			Factory.SetFocusedDockable(Documents, front);
		}
	}

	void CloseDocumentsExceptStart()
	{
		if (Documents?.VisibleDockables is null || Factory is null)
			return;
		foreach (var dockable in Documents.VisibleDockables.ToList())
		{
			if (dockable.Id == "start")
				continue;
			if (dockable is Dock.Model.Mvvm.Controls.Document document)
				document.CanClose = true;
			Factory.CloseDockable(dockable);
		}
	}

	void CloseStartPage()
	{
		if (Documents?.VisibleDockables?.FirstOrDefault(d => d.Id == "start") is { } start && Factory is not null)
			Factory.CloseDockable(start);
	}

	/// <summary>Opens (or refocuses) the review overview document.</summary>
	public void OpenOverview()
	{
		ShowDocument("overview", () => new Documents.OverviewDocumentViewModel(this));
		// So the key that got here also gets back: the overview handles it itself, and only
		// while it holds focus.
		Avalonia.Threading.Dispatcher.UIThread.Post(
			() => global::Stampeded.Documents.OverviewDocumentView.Current?.Focus(),
			Avalonia.Threading.DispatcherPriority.Loaded);
	}

	/// <summary>Closes the document tab in front, which is what its own X does.</summary>
	public void CloseActiveDocument()
	{
		if (Documents?.ActiveDockable is { } active && Factory is not null)
			Factory.CloseDockable(active);
	}

	/// <summary>Every comment of this review under the lines it is about, with the verdict.</summary>
	public void OpenReviewDocument()
		=> ShowDocument("review", () => new Documents.ReviewDocumentViewModel(this) { Title = "Review" });

	/// <summary>Two arbitrary texts side by side (e.g. base-vs-head test run outputs).</summary>
	public void OpenSideBySideText(string id, string title, string leftText, string rightText)
	{
		// Content is per-run: replace any previous document under this id instead of
		// reactivating it with stale text.
		if (Factory is not null && Documents?.VisibleDockables?.OfType<SideBySideDocumentViewModel>()
				.FirstOrDefault(d => d.Id == id) is { } stale)
		{
			Factory.CloseDockable(stale);
		}
		var stub = new FileDiff(title, title, FileChangeKind.Modified, false, []);
		ShowDocument(id, () => new SideBySideDocumentViewModel(stub, DiffDocumentBuilder.BuildPair(leftText, rightText)) {
			Title = title,
		});
	}

	/// <summary>Opens (or activates) a unified patch as a coloured diff tab. Historical:
	/// the document spans several files, so no line of it maps to a blob of the review.</summary>
	public void OpenPatchDocument(string id, string title, string patch, string sha)
	{
		var stub = new FileDiff(title, title, FileChangeKind.Modified, false, []);
		ShowDocument(id, () => new DiffDocumentViewModel(stub, PatchDocumentBuilder.Build(patch)) {
			Title = title,
			Historical = true,
			HistoricalSha = sha,
			IsPatch = true,
		});
	}

	/// <summary>Opens (or activates) a plain text document tab (CI logs, reports, ...).</summary>
	public void OpenTextDocument(string id, string title, string text)
	{
		ShowDocument(id, () => new TextDocumentViewModel(text) { Title = title });
	}

	public async Task OpenAdjacentFileAsync(int delta)
	{
		var order = ReadingOrder;
		if (order.Count == 0)
			return;
		int index = 0;
		var current = CurrentFile;
		if (current is not null)
		{
			int i = order.ToList().FindIndex(f => f.Path == current.Path);
			index = Math.Clamp(i + delta, 0, order.Count - 1);
			if (i >= 0 && index == i)
				return;
		}
		if (await OpenFileAsync(order[index]) is { } opened)
			FocusEditorOf(opened);
	}

	/// <summary>
	/// Puts keyboard focus in a document's editor once the dock has shown it. The single-key
	/// review gestures are handled by the editor, so advancing to the next file without this
	/// leaves them dead until the mouse is used: activating a dockable decides what is
	/// visible, not what the keyboard talks to.
	/// </summary>
	static void FocusEditorOf(Documents.IDiffDocument document)
		=> Avalonia.Threading.Dispatcher.UIThread.Post(
			() => {
				if (document is DiffDocumentViewModel unified)
					global::Stampeded.Documents.DiffDocumentView.ViewFor(unified)?.FocusEditor();
			},
			Avalonia.Threading.DispatcherPriority.Loaded);

	public async Task ToggleViewedAndAdvanceAsync()
	{
		var file = CurrentFile;
		if (file is null)
		{
			// Pressed where there is no file to mark - the overview, or a tab that is not a
			// diff. The key means "on to the next thing to read" wherever it is pressed, and
			// from here that is the first file still unread. With nothing left unread there is
			// nothing to go on to, and opening a file that was already read would be a loop.
			if (FirstUnread() is { } start && await OpenFileAsync(start) is { } opened)
			{
				FocusEditorOf(opened);
			}
			else if (Files.Count > 0)
			{
				// Nothing left to read is the end of the pass wherever the key was pressed,
				// and what follows a pass is saying something about it.
				OpenReviewDocument();
				StatusMessage?.Invoke($"All {Files.Count} file(s) here are marked viewed.");
			}
			return;
		}
		bool viewed = !Store.IsViewed(file.Path);
		Store.SetViewed(file.Path, viewed);
		ViewedChanged?.Invoke(file.Path, viewed);
		if (!viewed)
			return;
		// The end of what is being read: the last file of the list, or the last one of it that
		// was still unread. Either way there is nothing left below to advance into.
		var order = ReadingOrder;
		bool through = order.Count > 0
			&& (order[^1].Path == file.Path || order.All(f => Store.IsViewed(f.Path)));
		if (through && Scopes.Commit is not null)
		{
			int unread = Files.Count(f => !Store.IsViewed(f.Path));
			if (Scopes.CommitIndex + 1 < Scopes.Series.Count)
			{
				await Scopes.StepCommitAsync(1);
				// Straight into the next commit's first file. Stepping on its own opens the
				// overview, which is right when the step was asked for - the message is worth
				// reading before the diff - but reading the series with 'v' is one continuous
				// pass, and a stop at the overview between every commit is a key pressed for
				// nothing.
				if (ReadingOrder is [var firstOfCommit, ..] && await OpenFileAsync(firstOfCommit) is { } first)
					FocusEditorOf(first);
				if (unread > 0)
					StatusMessage?.Invoke($"Moved on with {unread} file(s) of that commit still unread.");
				return;
			}
			// The series is read: the next thing is the verdict. Leaving the scope first is
			// part of that - a review is submitted for the whole change, so a verdict page
			// reached while still inside one commit is a page whose buttons cannot work.
			await Scopes.ExitAsync();
			OpenReviewDocument();
			StatusMessage?.Invoke("Last commit read; back to the whole change, where the verdict is given.");
			return;
		}
		// Outside a scope the last file has nowhere to advance to, and the advance would
		// silently do nothing - leaving 'v' pressed once more to un-view the file just
		// finished. The read is over at that point, so it ends on the page where a verdict is
		// given, which is what 'n' off the end of the last file does as well.
		if (order.Count > 0 && order[^1].Path == file.Path)
		{
			OpenReviewDocument();
			StatusMessage?.Invoke($"Last file read; {Files.Count(f => Store.IsViewed(f.Path))} of {Files.Count} viewed.");
			return;
		}
		await OpenAdjacentFileAsync(1);
	}

	/// <summary>The file a pass continues at: the first one not marked viewed. Null once every
	/// one of them has been - a pass that is over has nowhere to continue.</summary>
	FileDiff? FirstUnread() => ReadingOrder.FirstOrDefault(f => !Store.IsViewed(f.Path));

	string? fileBeforeOverview;

	/// <summary>
	/// Swaps between the overview and the file being read, one key both ways. Looking up what
	/// the change is about mid-file is a glance, not a navigation: coming back has to land
	/// where it left, and picking the file out of the list again is not that.
	/// </summary>
	public async Task ToggleOverviewAsync()
	{
		if (Documents?.ActiveDockable is OverviewDocumentViewModel)
		{
			var back = Files.FirstOrDefault(f => f.Path == fileBeforeOverview)
				// Nothing to return to (the file went away with a reload, or the overview was
				// the first thing opened): the file to read is the first unread one, or simply
				// the first - this key always lands back in a file.
				?? FirstUnread() ?? Files.FirstOrDefault();
			if (back is not null && await OpenFileAsync(back) is { } opened)
				FocusEditorOf(opened);
			return;
		}
		fileBeforeOverview = CurrentFile?.Path;
		OpenOverview();
	}

	public void SetViewed(string path, bool viewed)
	{
		if (Store.IsViewed(path) == viewed)
			return;
		Store.SetViewed(path, viewed);
		ViewedChanged?.Invoke(path, viewed);
	}

	#region Semantic navigation

	/// <summary>The workspace serving one side of a diff: head for context/added lines,
	/// base for removed lines.</summary>
	public RoslynWorkspaceService? SemanticsFor(bool oldSide) => oldSide ? BaseSemantics : Semantics;

	static bool IsReady(RoslynWorkspaceService? sem)
		=> sem is { State: SemanticState.Ready or SemanticState.SyntaxOnly };

	/// <summary>Whether head-side semantics have finished loading. A review opens as soon as
	/// its diff is read, so the commands that need a compilation are offered only once there
	/// is one: asked earlier they cannot answer "not yet", only "no definition found", which
	/// reads as a fact about the code.</summary>
	public bool SemanticsReady => IsReady(Semantics);

	async Task<Microsoft.CodeAnalysis.ISymbol?> SymbolAtAsync(bool oldSide, string relPath, int line, int column)
	{
		var sem = SemanticsFor(oldSide);
		if (!IsReady(sem))
		{
			StatusMessage?.Invoke(oldSide
				? "Base-side semantics are still loading; navigation from removed lines is not available yet."
				: "Semantics are still loading; navigation is not available yet.");
			return null;
		}
		int? position = await sem!.GetPositionAsync(relPath, line, column, CancellationToken.None);
		if (position is null)
			return null;
		return await sem.GetSymbolAtAsync(relPath, position.Value, CancellationToken.None);
	}

	/// <summary>Go to definition of the symbol at a blob (line, column). For removed lines
	/// this resolves in the BASE workspace and navigation lands in base-side views.</summary>
	public async Task NavigateToDefinitionAsync(string relPath, int line, int column, bool oldSide, NavEntryOrigin origin)
	{
		var sem = SemanticsFor(oldSide);
		var symbol = await SymbolAtAsync(oldSide, relPath, line, column);
		if (symbol is null)
			return;
		var location = sem!.GetDefinitionLocation(symbol);
		if (location is null)
		{
			await OpenDecompiledDefinitionAsync(sem, symbol, origin);
			return;
		}
		string? targetRel = sem.ToRelativePath(location.FilePath);
		if (targetRel is null)
			return;
		CliLog.Write("action", $"goto definition: {targetRel}:{location.Line}{(oldSide ? " (base)" : "")}");
		RecordOrigin(origin);
		await NavigateToFileLineAsync(targetRel, location.Line, oldSide, record: true);
	}

	/// <summary>Definition view for a symbol without source: decompile its top-level
	/// containing type from the referenced assembly and jump to the member.</summary>
	async Task OpenDecompiledDefinitionAsync(RoslynWorkspaceService sem, Microsoft.CodeAnalysis.ISymbol symbol, NavEntryOrigin origin)
	{
		var original = symbol.OriginalDefinition;
		var topType = original as Microsoft.CodeAnalysis.INamedTypeSymbol ?? original.ContainingType;
		while (topType?.ContainingType is { } outer)
			topType = outer;
		string? assemblyPath = topType is null ? null : sem.TryGetMetadataAssemblyPath(topType);
		if (topType is null || assemblyPath is null)
		{
			StatusMessage?.Invoke($"'{symbol.Name}' has no source and its defining assembly could not be resolved.");
			return;
		}
		string reflectionName = topType.ContainingNamespace is { IsGlobalNamespace: false } ns
			? ns.ToDisplayString() + "." + topType.MetadataName
			: topType.MetadataName;
		using var busy = Busy.Begin($"Decompiling {topType.Name}");
		try
		{
			int token = original.MetadataToken;
			var result = await Task.Run(() => DecompilationService.DecompileType(assemblyPath, reflectionName, token));
			string id = $"decomp:{reflectionName}";
			var vm = ShowDocument(id, () => {
				var doc = DiffDocumentViewModel.ForSource(topType.Name + ".cs", result.Text);
				doc.Title = topType.Name + " [decompiled]";
				return doc;
			});
			if (vm is null)
				return;
			CliLog.Write("action", $"decompiled {reflectionName} ({Path.GetFileName(assemblyPath)}) -> line {result.MemberLine}");
			RecordOrigin(origin);
			history.Record(new NavEntry(id, result.MemberLine, false));
			vm.RequestCaret(result.MemberLine);
		}
		catch (Exception ex)
		{
			StatusMessage?.Invoke($"Decompiling {topType.Name} failed: {ex.Message}");
			CliLog.Write("action", $"decompile {reflectionName} FAILED: {ex.Message}");
		}
	}

	public async Task ShowReferencesAtAsync(string relPath, int line, int column, bool oldSide)
	{
		var sem = SemanticsFor(oldSide);
		var symbol = await SymbolAtAsync(oldSide, relPath, line, column);
		if (symbol is null)
			return;
		var hits = await sem!.FindReferencesAsync(symbol, CancellationToken.None);
		var items = hits
			.Select(h => (Rel: sem.ToRelativePath(h.FilePath), Hit: h))
			.Where(x => x.Rel is not null)
			.Select(x => new ReferenceItem(
				x.Rel!, x.Hit.Line, x.Hit.LineText,
				!oldSide && changed.IsAdded(x.Rel!, x.Hit.Line),
				oldSide))
			.ToList();
		ReferencesAvailable?.Invoke(symbol.Name + (oldSide ? " (base)" : ""), items);
		Factory?.ShowPane("References");
	}

	/// <summary>Root of a call graph: the symbol at a blob position, named for display.</summary>
	public sealed record CallRoot(string Display, string RelPath, int Line, int Column, bool OldSide);

	public event Action<CallRoot>? CallGraphRequested;

	public event Action<string>? CallGraphFailed;

	/// <summary>Asks the call-graph pane to root itself at a blob position.</summary>
	public async Task RequestCallGraphAsync(string relPath, int line, int column, bool oldSide)
	{
		var sem = SemanticsFor(oldSide);
		if (!IsReady(sem))
		{
			CallGraphFailed?.Invoke("Semantics are not loaded yet, so calls cannot be resolved.");
			return;
		}
		var symbol = await sem!.GetSymbolOnLineAsync(relPath, line, column, CancellationToken.None);
		if (symbol is null)
		{
			CallGraphFailed?.Invoke($"No symbol found on {System.IO.Path.GetFileName(relPath)}:{line}.");
			return;
		}
		CallGraphRequested?.Invoke(new CallRoot(
			symbol.ToDisplayString(Microsoft.CodeAnalysis.SymbolDisplayFormat.CSharpShortErrorMessageFormat),
			relPath, line, column, oldSide));
	}

	/// <summary>One level of the call hierarchy at a blob position. Paths come back
	/// repo-relative, and nodes outside the worktree (or without source) cannot be
	/// expanded further.</summary>
	public async Task<IReadOnlyList<CallGraphItem>> GetCallsAsync(
		string relPath, int line, int column, bool oldSide, CallDirection direction)
	{
		var sem = SemanticsFor(oldSide);
		if (!IsReady(sem))
			return [];
		var symbol = await sem!.GetSymbolOnLineAsync(relPath, line, column, CancellationToken.None);
		if (symbol is null)
			return [];
		var nodes = await sem.GetCallsAsync(symbol, direction, CancellationToken.None);
		return nodes
			.Select(n => new CallGraphItem(
				n,
				n.FilePath is null ? null : sem.ToRelativePath(n.FilePath),
				oldSide,
				IsChangedMember(n.FilePath is null ? null : sem.ToRelativePath(n.FilePath), n.Display),
				[.. n.Sites
					.Select(s => (Rel: sem.ToRelativePath(s.FilePath), Site: s))
					.Where(x => x.Rel is not null)
					.Select(x => new CallSiteItem(x.Rel!, x.Site.Line, x.Site.Preview, oldSide))]))
			.ToList();
	}

	/// <summary>Whether this member is one the review changes. The change map names
	/// members in the same display format, so they compare directly.</summary>
	public bool IsChangedMember(string? relPath, string display)
		=> relPath is { Length: > 0 }
			&& ChangeMap.Any(e => e.RelPath == relPath && e.Display == display);

	/// <summary>One call, at a repo-relative position.</summary>
	public sealed record CallSiteItem(string RelPath, int Line, string Preview, bool OldSide);

	/// <summary>A call-graph node paired with the repo-relative paths it lives at.</summary>
	public sealed record CallGraphItem(
		CallNode Node, string? RelPath, bool OldSide, bool IsChanged, IReadOnlyList<CallSiteItem> Sites)
	{
		public bool CanExpand => RelPath is { Length: > 0 };

		public string Display => Sites.Count > 1 ? $"{Node.Display}  ({Sites.Count}x)" : Node.Display;

		public string Detail => RelPath is { Length: > 0 } path
			? $"{Node.ContainingType}  -  {path}:{Node.Line}"
			: $"{Node.ContainingType}  -  no source";
	}

	/// <summary>Occurrences of the symbol at the given blob position within its own file.</summary>
	public async Task<IReadOnlyList<SemanticToken>> FindOccurrencesAsync(string relPath, int line, int column, bool oldSide)
	{
		var sem = SemanticsFor(oldSide);
		var symbol = await SymbolAtAsync(oldSide, relPath, line, column);
		if (symbol is null)
			return [];
		return await sem!.FindOccurrencesInFileAsync(symbol, relPath, CancellationToken.None);
	}

	/// <summary>
	/// Every file the head revision has, repository-relative, read once per head. The list is
	/// the revision's rather than a checkout's, so it holds before a worktree exists and never
	/// offers a file that only some working tree has.
	/// </summary>
	public async Task<IReadOnlyList<string>> ListHeadFilesAsync(CancellationToken ct = default)
	{
		if (HeadSha is not { } head)
			return [];
		if (headFiles is { } cached && headFilesFor == head)
			return cached;
		try
		{
			var files = await Git.ListFilesAsync(head, ct);
			headFiles = files;
			headFilesFor = head;
			return files;
		}
		catch (ToolFailedException)
		{
			// A revision the object database cannot list is one nothing else could read
			// either; the caller shows what it has.
			return [];
		}
	}

	/// <summary>Source declarations matching a name pattern, for going to one. Answers empty
	/// while semantics are still loading rather than waiting for them: this runs against every
	/// keystroke, and a box that stops responding is worse than one that fills in late.</summary>
	public Task<IReadOnlyList<Core.Roslyn.DeclarationHit>> FindDeclarationsAsync(
		string pattern, int max, CancellationToken ct)
		=> IsReady(Semantics)
			? Semantics!.FindDeclarationsAsync(pattern, max, ct)
			: Task.FromResult<IReadOnlyList<Core.Roslyn.DeclarationHit>>([]);

	IReadOnlyList<string>? headFiles;
	string? headFilesFor;

	/// <summary>Opens (or activates) a file at a blob line. Head side: the review diff when
	/// the file is in the PR, else a head source view. Base side: the review diff mapped via
	/// the old line, else a base source view.</summary>
	public async Task NavigateToFileLineAsync(string relPath, int fileLine, bool oldSide, bool record)
	{
		Documents.IDiffDocument? vm;
		var fileDiff = oldSide
			? Files.FirstOrDefault(f => f.OldPath == relPath)
			: Files.FirstOrDefault(f => f.Path == relPath);
		if (fileDiff is not null)
		{
			vm = await OpenFileAsync(fileDiff);
		}
		else
		{
			// Read from the object database rather than a checkout: the revision is what the
			// file is wanted at, and a review has one of those before it has anywhere on disk.
			string? text = oldSide
				? BaseSha is { } baseSha ? await Blobs.ReadAsync(baseSha, relPath) : null
				: await ReadFileAtHeadAsync(relPath);
			if (text is null)
			{
				StatusMessage?.Invoke($"{relPath} is not in the {(oldSide ? "base" : "head")} revision.");
				return;
			}
			string prefix = oldSide ? "srcbase:" : "src:";
			vm = ShowDocument(prefix + relPath, () => {
				var source = DiffDocumentViewModel.ForSource(relPath, text);
				if (oldSide)
					source.Title = source.Title + " @ base";
				return source;
			});
		}
		if (vm is null)
			return;
		if (record && vm.Id is { Length: > 0 } dockableId)
			history.Record(new NavEntry(dockableId, fileLine, oldSide));
		vm.RequestCaret(fileLine, oldSide);
	}

	public readonly record struct NavEntryOrigin(string DockableId, int BlobLine, bool OldSide);

	void RecordOrigin(NavEntryOrigin origin)
		=> history.Record(new NavEntry(origin.DockableId, origin.BlobLine, origin.OldSide));

	public Task GoBackAsync() => history.CanNavigateBack ? NavigateToEntryAsync(history.GoBack()) : Task.CompletedTask;

	public Task GoForwardAsync() => history.CanNavigateForward ? NavigateToEntryAsync(history.GoForward()) : Task.CompletedTask;

	async Task NavigateToEntryAsync(NavEntry entry)
	{
		if (entry.DockableId.StartsWith("decomp:", StringComparison.Ordinal))
		{
			// Decompiled tabs are only revisited while still open; a closed one would
			// need the assembly path again, which history does not carry.
			var doc = Documents?.VisibleDockables?
				.OfType<DiffDocumentViewModel>()
				.FirstOrDefault(d => d.Id == entry.DockableId);
			if (doc is not null && Factory is not null && Documents is not null)
			{
				Factory.SetActiveDockable(doc);
				Factory.SetFocusedDockable(Documents, doc);
				doc.RequestCaret(entry.BlobLine, entry.OldSide);
			}
			return;
		}
		string relPath = entry.DockableId[(entry.DockableId.IndexOf(':') + 1)..];
		if (entry.DockableId.StartsWith("diff:", StringComparison.Ordinal))
		{
			var fileDiff = Files.FirstOrDefault(f => f.Path == relPath);
			if (fileDiff is not null)
			{
				var vm = await OpenFileAsync(fileDiff);
				vm?.RequestCaret(entry.BlobLine, entry.OldSide);
				return;
			}
		}
		bool oldSide = entry.DockableId.StartsWith("srcbase:", StringComparison.Ordinal);
		string? text = oldSide
			? BaseSha is { } baseSha ? await Blobs.ReadAsync(baseSha, relPath) : null
			: await ReadFileAtHeadAsync(relPath);
		if (text is null)
			return;
		var source = ShowDocument(entry.DockableId, () => {
			var vm = DiffDocumentViewModel.ForSource(relPath, text);
			if (oldSide)
				vm.Title += " @ base";
			return vm;
		});
		source?.RequestCaret(entry.BlobLine, entry.OldSide);
	}

	#endregion

	#region Change map

	public sealed record ChangeMapEntry(string RelPath, string Project, string Kind, string Display, int Line, bool OldSide, string MemberKind = "");

	public IReadOnlyList<ChangeMapEntry> ChangeMap { get; private set; } = [];
	public event Action? ChangeMapChanged;
	bool changeMapComputed;

	/// <summary>True once the change map for this review has been computed - distinguishes
	/// "legitimately empty" from "semantics not ready yet".</summary>
	public bool ChangeMapComputed => changeMapComputed;

	/// <summary>Symbol-level inventory of the diff: which members were added/modified
	/// (head side) or removed (base side). Computed once per review when the respective
	/// semantic workspace is ready.</summary>
	public async Task ComputeChangeMapAsync()
	{
		if (changeMapComputed || Semantics is not { State: SemanticState.Ready or SemanticState.SyntaxOnly } head)
			return;
		changeMapComputed = true;
		var entries = new List<ChangeMapEntry>();
		foreach (var file in Files)
		{
			if (!file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
				continue;
			string project = file.Path.Split('/')[0];
			var baseSem = BaseSemantics is { State: SemanticState.Ready or SemanticState.SyntaxOnly } b ? b : null;
			var added = changed.Added(file.Path);
			var headMembers = added.Count > 0
				? await head.MapLinesToMembersAsync(file.Path, added, CancellationToken.None)
				: [];
			var baseDisplays = baseSem is not null && file.Kind != FileChangeKind.Added
				? await baseSem.ListMemberDisplaysAsync(file.OldPath, CancellationToken.None)
				: new HashSet<string>();
			foreach (var member in headMembers)
			{
				string kind = baseDisplays.Contains(member.Display) ? "Modified" : "Added";
				entries.Add(new ChangeMapEntry(file.Path, project, kind, member.Display, member.FirstLine, false, member.Kind));
			}
			if (baseSem is not null && changed.Removed(file.OldPath) is { Count: > 0 } removed)
			{
				var baseMembers = await baseSem.MapLinesToMembersAsync(file.OldPath, removed, CancellationToken.None);
				var headDisplays = file.Kind != FileChangeKind.Deleted
					? await head.ListMemberDisplaysAsync(file.Path, CancellationToken.None)
					: new HashSet<string>();
				foreach (var member in baseMembers)
				{
					if (!headDisplays.Contains(member.Display))
						entries.Add(new ChangeMapEntry(file.OldPath, project, "Removed", member.Display, member.FirstLine, true, member.Kind));
				}
			}
		}
		// A removed and an added entry sharing a simple name are one member whose
		// SIGNATURE changed, not a removal plus an addition - fold each such pair into
		// the added entry as Modified. Pairing is by simple name (overload-count
		// balanced), so genuinely deleted overloads still surface as Removed.
		var removedByName = entries.Where(e => e.Kind == "Removed")
			.GroupBy(e => MemberSimpleName(e.Display))
			.ToDictionary(g => g.Key, g => new Queue<ChangeMapEntry>(g));
		var folded = new List<ChangeMapEntry>(entries.Count);
		var consumed = new HashSet<ChangeMapEntry>();
		foreach (var entry in entries)
		{
			if (entry.Kind == "Added"
				&& removedByName.TryGetValue(MemberSimpleName(entry.Display), out var partners)
				&& partners.Count > 0)
			{
				consumed.Add(partners.Dequeue());
				folded.Add(entry with { Kind = "Modified" });
			}
			else
			{
				folded.Add(entry);
			}
		}
		entries = folded.Where(e => !consumed.Contains(e)).ToList();
		ChangeMap = entries;
		ChangeMapChanged?.Invoke();
		CliLog.Write("semantics", $"change map: {entries.Count} member(s)");
	}

	void ResetChangeMap()
	{
		changeMapComputed = false;
		ChangeMap = [];
		ChangeMapChanged?.Invoke();
	}

	#endregion

	string? defaultBranch;

	/// <summary>
	/// The repository's default branch ("master", "main", ...). GitHub is asked first,
	/// because it is the authority; git's local `origin/HEAD` is only a clone-time snapshot
	/// and is missing or stale often enough to matter. Cached for the process - a repository
	/// does not change its default branch while it is being reviewed.
	/// </summary>
	public async Task<string> GetDefaultBranchAsync()
	{
		if (defaultBranch is { } known)
			return known;
		try
		{
			return defaultBranch = await GitHub.GetDefaultBranchAsync();
		}
		catch (ToolFailedException)
		{
			string local = await Git.GetDefaultBaseAsync();
			return defaultBranch = local.StartsWith("origin/", StringComparison.Ordinal)
				? local["origin/".Length..]
				: local;
		}
	}

	/// <summary>The ref to review and rebase against: the default branch as origin has it.</summary>
	public async Task<string> GetDefaultBaseAsync() => "origin/" + await GetDefaultBranchAsync();

	/// <summary>
	/// Merges the current pull request after asking, and says what happened either way.
	/// The question names the branches and the method because this is the one command in the
	/// tool that changes what everyone else sees and cannot be taken back from here.
	/// </summary>
	public async Task<string> MergeCurrentPrAsync(string method)
	{
		if (CurrentPr is not { } pr)
			return "No pull request is open.";
		if (Offline)
		{
			return $"Offline: this review was opened from a snapshot taken {OfflineSince:g}. "
				+ "Whether it would merge is not something a snapshot can say; reload (F5) first.";
		}
		Core.GitHub.MergeState state;
		try
		{
			state = await GitHub.GetMergeStateAsync(pr.Number);
		}
		catch (ToolFailedException ex)
		{
			return $"Could not read the merge state: {ex.Message}";
		}
		if (!state.CanMerge)
			return $"GitHub will not merge #{pr.Number} right now ({state.Describe}).";
		if (MainWindowOrNull() is not { } owner)
			return "";
		bool merge = await new ConfirmWindow("Merge pull request",
			$"#{pr.Number} {pr.Title}\n\n"
				+ $"{pr.HeadRefName}  ->  {pr.BaseRefName}, by {method}.\n\n"
				+ (LocalHead
					? $"This merges {PrHeadSha![..9]}, what GitHub has - not the local branch you have "
						+ "been reading, which is ahead of it.\n\n"
					: "")
				+ "This merges on GitHub, for everyone. It cannot be undone from here.",
			$"Merge ({method})").ShowDialog<bool>(owner);
		if (!merge)
			return $"#{pr.Number} not merged.";
		try
		{
			using var busy = Busy.Begin($"Merging #{pr.Number}");
			await GitHub.MergePrAsync(pr.Number, method);
			CliLog.Write("action", $"merged #{pr.Number} by {method}");
			return $"#{pr.Number} merged into {pr.BaseRefName} by {method}.";
		}
		catch (ToolFailedException ex)
		{
			return $"Merge failed: {ex.Message}";
		}
	}

}
