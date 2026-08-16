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
	{
		if (Coverage is null || !Coverage.TryGetValue(path, out var hits)
			|| !addedLinesByFile.TryGetValue(path, out var added))
			return (0, 0);
		int uncovered = 0, measured = 0;
		foreach (var line in added)
		{
			if (!hits.TryGetValue(line, out int h))
				continue;
			measured++;
			if (h == 0)
				uncovered++;
		}
		return (uncovered, measured);
	}

	public bool IsUncoveredAdded(string path, int newLine)
		=> Coverage is not null
			&& addedLinesByFile.TryGetValue(path, out var added) && added.Contains(newLine)
			&& Coverage.TryGetValue(path, out var hits) && hits.TryGetValue(newLine, out int h) && h == 0;

	/// <summary>Test classes referencing the change map's members, for a focused test
	/// filter ("run the tests that matter for this change").</summary>
	public async Task<IReadOnlyList<string>> SuggestImpactedTestClassesAsync()
	{
		if (Semantics is not { State: SemanticState.Ready or SemanticState.SyntaxOnly } sem)
			return [];
		var classes = new HashSet<string>();
		int traced = 0, unresolved = 0;
		foreach (var entry in ChangeMap.Where(e => !e.OldSide).Take(30))
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
		// Which of the ways this can come up empty actually happened: no member resolved, or
		// members resolved but nothing under test refers to them.
		CliLog.Write("impacted", $"{traced} member(s) traced of {ChangeMap.Count(e => !e.OldSide)} changed"
			+ (unresolved > 0 ? $" ({unresolved} unresolved)" : "")
			+ $" -> {classes.Count} test class(es)"
			+ (classes.Count > 0 ? ": " + string.Join(", ", classes) : ""));
		return classes.ToList();

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
		if (Coverage is null)
			return (0, 0);
		int uncovered = 0, measured = 0;
		foreach (var (path, added) in addedLinesByFile)
		{
			if (!Coverage.TryGetValue(path, out var hits))
				continue;
			foreach (var line in added)
			{
				if (!hits.TryGetValue(line, out int h))
					continue;
				measured++;
				if (h == 0)
					uncovered++;
			}
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
	Dictionary<string, HashSet<int>> addedLinesByFile = [];
	Dictionary<string, HashSet<int>> removedLinesByFile = [];
	readonly NavigationHistory<NavEntry> history = new();

	/// <summary>Opens a review of a local base..head range (no PR: checks, posted comments
	/// and review submission stay empty/disabled; everything else works identically).</summary>
	public async Task OpenLocalRangeAsync(string baseRef, string headRef)
	{
		sessionCts?.Cancel();
		var cts = sessionCts = new CancellationTokenSource();
		var ct = cts.Token;

		using var busy = Busy.Begin($"Opening {baseRef}..{headRef}");
		CliLog.Write("action", $"open local range {baseRef}..{headRef}");
		string headSha = await ResolveAsync(headRef, ct);
		string baseSha = await Git.GetMergeBaseAsync(await ResolveAsync(baseRef, ct), headSha, ct);
		DirtyWorktreePath = await FindDirtyCheckoutAsync(headRef, ct);
		var committed = await Git.DiffAsync(baseSha, headSha, ct);
		var files = DirtyWorktreePath is { } dirty
			? await Git.DiffWorkingTreeAsync(dirty, baseSha, ct)
			: committed;
		UncommittedFileCount = Math.Max(0, files.Count - committed.Count);
		ct.ThrowIfCancellationRequested();

		ResetScope();
		Reviewers = null;
		CurrentPr = null;
		LocalRange = (baseRef, headRef);
		BaseSha = baseSha;
		HeadSha = headSha;
		Files = files;
		IndexAddedLines(files);
		Store.OpenLocal(Path.GetFileName(RepoPath), $"{baseRef}..{headRef}", headSha, baseSha);
		await ApplyReReviewCarryOverAsync(ct);
		await PinReviewHeadsAsync(ct);
		ComputeChurnAsync().HandleExceptions();
		history.Clear();
		CloseDocumentsExceptStart();
		PostedComments = [];
		CommentsLoaded = true;
		ReviewChanged?.Invoke();
		// The overview is where a review starts; files open as the Explorer's list is walked,
		// one tab at a time, instead of arriving as a wall of them.
		OpenOverview();
		CloseStartPage();
		LoadIssueUrlPrefixAsync(ct).HandleExceptions();
		LoadSemanticsAsync(headSha, baseSha, ct).HandleExceptions();
		LoadGeneratedSourcesAsync(ct).HandleExceptions();
		ReattachDraftsAsync(ct).HandleExceptions();
		CommentsChanged?.Invoke();
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
		var detail = await GitHub.GetPrAsync(number, ct);
		string headSha = await Git.FetchPrHeadAsync(number, ct);
		await Git.FetchBranchAsync(detail.BaseRefName, ct);
		string baseSha = await Git.GetMergeBaseAsync($"origin/{detail.BaseRefName}", headSha, ct);
		DirtyWorktreePath = null;
		UncommittedFileCount = 0;
		var files = await Git.DiffAsync(baseSha, headSha, ct);
		ct.ThrowIfCancellationRequested();

		ResetScope();
		Reviewers = null;
		CurrentPr = detail;
		LocalRange = null;
		BaseSha = baseSha;
		HeadSha = headSha;
		Files = files;
		IndexAddedLines(files);
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
		LoadIssueUrlPrefixAsync(ct).HandleExceptions();
		LoadSemanticsAsync(headSha, baseSha, ct).HandleExceptions();
		LoadGeneratedSourcesAsync(ct).HandleExceptions();
		ReattachDraftsAsync(ct).HandleExceptions();
		LoadPostedCommentsAsync(number, ct).HandleExceptions();
		LoadReviewersAsync(number, ct).HandleExceptions();
	}

	async Task LoadIssueUrlPrefixAsync(CancellationToken ct)
	{
		IssueUrlPrefix = await GitHub.GetIssueUrlPrefixAsync(ct);
		// The description and the comment threads are rendered before this returns.
		ReviewChanged?.Invoke();
		CommentsChanged?.Invoke();
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
			await Task.Run(() => semantics.LoadAsync(WorktreePath, ct), ct);
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

	void IndexAddedLines(IReadOnlyList<FileDiff> files)
	{
		addedLinesByFile = [];
		removedLinesByFile = [];
		foreach (var file in files)
		{
			var added = new HashSet<int>();
			var removed = new HashSet<int>();
			foreach (var hunk in file.Hunks)
			{
				int newLine = hunk.NewStart;
				int oldLine = hunk.OldStart;
				foreach (var line in hunk.Lines)
				{
					if (line.Kind == PatchLineKind.Added)
						added.Add(newLine);
					if (line.Kind == PatchLineKind.Removed)
						removed.Add(oldLine);
					if (line.Kind != PatchLineKind.Removed)
						newLine++;
					if (line.Kind != PatchLineKind.Added)
						oldLine++;
				}
			}
			addedLinesByFile[file.Path] = added;
			removedLinesByFile[file.OldPath] = removed;
		}
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
		if (WorktreePath is not { } head || await EnsureBaseWorktreeAsync(ct) is not { } baseTree)
		{
			SetGeneratedStatus("no worktrees", done: true);
			return;
		}
		try
		{
			// The semantic load is a design-time build over these same trees; running a real
			// build alongside it has both writing the same obj directories. The A/B run waits
			// for the same reason.
			SetGeneratedStatus("waiting for semantics...", done: false);
			while (Semantics is { State: SemanticState.Restoring or SemanticState.Loading }
				|| BaseSemantics is { State: SemanticState.Restoring or SemanticState.Loading })
			{
				await Task.Delay(1000, ct);
			}
			SetGeneratedStatus("building head...", done: false);
			await GeneratedSources.BuildAsync(head, ct);
			if (GeneratedSources.Collect(head).Count == 0)
			{
				SetGeneratedStatus("no generators", done: true);
				return;
			}
			SetGeneratedStatus("building base...", done: false);
			await GeneratedSources.BuildAsync(baseTree, ct);
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

	public async Task<DiffDocumentViewModel?> OpenFileAsync(FileDiff file)
	{
		if (file.Generated is { } generated)
		{
			return ShowDocument("diff:" + file.Path, () => new DiffDocumentViewModel(
				file,
				DiffDocumentBuilder.Build(ReadOrEmpty(generated.BaseFile), ReadOrEmpty(generated.HeadFile))) {
				Title = Path.GetFileName(file.Path),
			});

			static string ReadOrEmpty(string? path) => path is null ? "" : File.ReadAllText(path);
		}
		if (BaseSha is null || HeadSha is null)
			return null;
		string oldText = file.Kind == FileChangeKind.Added || file.IsBinary
			? ""
			: await Git.ShowFileAsync(BaseSha, file.OldPath);
		string newText = file.Kind == FileChangeKind.Deleted || file.IsBinary
			? ""
			: await ReadHeadFileAsync(file.NewPath);
		return ShowDocument("diff:" + file.Path, () => new DiffDocumentViewModel(file, DiffDocumentBuilder.Build(oldText, newText)) {
			Title = Path.GetFileName(file.Path),
		});
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

	public FileDiff? CurrentFile => (Documents?.ActiveDockable as DiffDocumentViewModel)?.File;

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
		sinceLastPassTree = null;
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

	/// <summary>The key a local review's state file is named by: the range as it was opened,
	/// falling back to the resolved commits when the review was not opened from refs.</summary>
	string LocalRangeKey((string Base, string Head) range)
		=> LocalRange is { } local ? $"{local.Base}..{local.Head}" : $"{range.Base[..9]}..{range.Head[..9]}";

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
			string patch = await ExternalTool.RunAsync("git", ["diff", LastPassHead, HeadSha], RepoPath);
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
			string output = await ExternalTool.RunAsync("git", ["log", "--since=1.year", "--name-only", "--format="], RepoPath);
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
				counts[line] = counts.GetValueOrDefault(line) + 1;
			ChurnByFile = counts;
			ChurnChanged?.Invoke();
		}
		catch (ToolFailedException)
		{
			// Shallow or odd repos: triage simply shows no churn column.
		}
	}

	public int AddedLineCount(string path)
		=> addedLinesByFile.TryGetValue(path, out var lines) ? lines.Count : 0;

	public string GetDepth(string path) => Store.GetDepth(path);

	public void SetDepth(string path, string depth)
	{
		Store.SetDepth(path, depth);
		DepthChanged?.Invoke();
	}

	public event Action? DepthChanged;

	static string MemberSimpleName(string display)
	{
		int paren = display.IndexOf('(');
		string noArgs = paren < 0 ? display : display[..paren];
		int dot = noArgs.LastIndexOf('.');
		return dot < 0 ? noArgs : noArgs[(dot + 1)..];
	}

	/// <summary>Triage's bounce outcome as a first-class action: drafts a review body naming
	/// the cost, so declining a review is one click instead of a social confrontation.</summary>
	public void PrepareBounceBody()
	{
		var t = ComputeTriage();
		int changed = t.Rows.Sum(r => r.Added + r.Removed);
		if (CommentsPane is Panes.CommentsPaneViewModel comments)
		{
			comments.State.ReviewBody =
				$"Bouncing this for now: {changed} changed lines across {Files.Count} files " +
				$"is ~{t.Sittings} review sitting(s) (~{t.Minutes} min). Could this be split into smaller PRs? " +
				"Happy to review the pieces promptly.";
		}
		StatusMessage?.Invoke("Bounce comment drafted in the Comments pane (submit as COMMENT).");
	}

	#endregion

	/// <summary>Opens the side-by-side view of the active file (or a given one).</summary>
	public async Task OpenSideBySideAsync(FileDiff? file = null)
	{
		file ??= CurrentFile;
		if (file is null || BaseSha is null || HeadSha is null || Documents is null || Factory is null)
			return;
		string id = "sbs:" + file.Path;
		var existing = Documents.VisibleDockables?
			.OfType<SideBySideDocumentViewModel>()
			.FirstOrDefault(d => d.Id == id);
		if (existing is null)
		{
			string oldText = file.Kind == FileChangeKind.Added || file.IsBinary
				? ""
				: await Git.ShowFileAsync(BaseSha, file.OldPath);
			string newText = file.Kind == FileChangeKind.Deleted || file.IsBinary
				? ""
				: await ReadHeadFileAsync(file.NewPath);
			existing = new SideBySideDocumentViewModel(file, DiffDocumentBuilder.BuildPair(oldText, newText)) {
				Id = id,
				Title = Path.GetFileName(file.Path) + " (side-by-side)",
			};
			Factory.AddDockable(Documents, existing);
		}
		Factory.SetActiveDockable(existing);
		Factory.SetFocusedDockable(Documents, existing);
	}

	/// <summary>Removes cached worktrees except the current review's base and head.</summary>
	public async Task PruneWorktreeCacheAsync()
	{
		// The review's own base and head: inside a scope those are not what BaseSha and
		// HeadSha say, and keeping the scope's instead would delete the worktrees the
		// semantic workspaces are loaded from.
		var keep = new List<string>();
		if (ReviewRange is { } range)
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
				string patch = await ExternalTool.RunAsync("git", ["show", sha], RepoPath);
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

	#region Per-commit reading

	/// <summary>
	/// The commit being read on its own, when the change is being worked through one
	/// commit at a time instead of as a single diff. A well-made series is the author's
	/// own decomposition of the change, and following it is usually easier than reading
	/// every logic change at once.
	/// </summary>
	public CommitInfo? CommitScope { get; private set; }

	/// <summary>The commits of the review, oldest first - the order they were written in.</summary>
	public IReadOnlyList<CommitInfo> ScopeCommits { get; private set; } = [];

	public int CommitScopeIndex { get; private set; }

	(string Base, string Head)? fullRange;

	/// <summary>The range the review is of. BaseSha and HeadSha stop describing it while a
	/// single commit is in scope - they move to that commit - so anything that talks about
	/// the review as a whole has to ask here instead.</summary>
	public (string Base, string Head)? ReviewRange
		=> fullRange ?? (BaseSha is { } b && HeadSha is { } h ? (b, h) : null);

	public event Action? CommitScopeChanged;

	public bool CanEnterCommitScope => HeadSha is not null && DirtyWorktreePath is null;

	(string Base, string Head)? cachedCommitsRange;
	IReadOnlyList<CommitInfo> cachedCommits = [];
	Dictionary<string, (int Added, int Removed)>? cachedCommitStats;

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
			cachedCommits = await Git.LogAsync($"{range.Base}..{range.Head}", null, follow: false, limit: 200, ct);
			cachedCommitStats = null;
			cachedCommitsRange = range;
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
		return await Git.LogAsync($"{range.Base}..{range.Head}", null, follow: false, limit: 200, ct);
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
		await GetRangeCommitsAsync(ct);
		if (cachedCommitStats is not null || ReviewRange is not { } range)
			return cachedCommitStats ?? [];
		var stats = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
		string output = await ExternalTool.RunAsync(
			"git", ["log", "--format=%H", "--shortstat", $"{range.Base}..{range.Head}"], RepoPath, ct);
		string? sha = null;
		foreach (var line in output.ReplaceLineEndings("\n").Split('\n'))
		{
			string trimmed = line.Trim();
			if (trimmed.Length == 40 && trimmed.All(char.IsAsciiHexDigit))
			{
				sha = trimmed;
			}
			else if (sha is not null && trimmed.Contains("changed", StringComparison.Ordinal))
			{
				var insertions = System.Text.RegularExpressions.Regex.Match(trimmed, @"(\d+) insertion");
				var deletions = System.Text.RegularExpressions.Regex.Match(trimmed, @"(\d+) deletion");
				stats[sha] = (
					insertions.Success ? int.Parse(insertions.Groups[1].Value) : 0,
					deletions.Success ? int.Parse(deletions.Groups[1].Value) : 0);
			}
		}
		cachedCommitStats = stats;
		return stats;
	}

	/// <summary>Reads the review one commit at a time, starting at the oldest.</summary>
	public async Task EnterCommitScopeAsync(int index = 0)
	{
		// The two scopes are alternatives, not layers: the since-last-pass scope diffs
		// against a tree, which has no history for a commit list to come from.
		if (InSinceLastPassScope)
			await ExitScopeAsync();
		if (ReviewRange is not { } range)
			return;
		(string baseSha, string headSha) = range;
		if (ScopeCommits.Count == 0)
		{
			// Oldest first: the series is meant to be read in the order it was written.
			ScopeCommits = [.. (await GetRangeCommitsAsync()).Reverse()];
		}
		if (ScopeCommits.Count == 0)
		{
			StatusMessage?.Invoke("This review has no commits to step through.");
			return;
		}
		fullRange ??= (baseSha, headSha);
		await ApplyCommitScopeAsync(Math.Clamp(index, 0, ScopeCommits.Count - 1));
	}

	public Task StepCommitScopeAsync(int direction)
		=> CommitScope is null
			? Task.CompletedTask
			: ApplyCommitScopeAsync(Math.Clamp(CommitScopeIndex + direction, 0, ScopeCommits.Count - 1));

	async Task ApplyCommitScopeAsync(int index)
	{
		var commit = ScopeCommits[index];
		CommitScopeIndex = index;
		CommitScope = commit;
		// The parent came with the commit: asking git for it is a process per step, and the
		// log that listed the series already said what each one was written on top of.
		string parent = commit.FirstParent ?? await ResolveAsync($"{commit.Sha}^", CancellationToken.None);
		BaseSha = parent;
		HeadSha = commit.Sha;
		Files = await Git.DiffAsync(parent, commit.Sha);
		IndexAddedLines(Files);
		Store.OpenCommitScope(Path.GetFileName(RepoPath), commit.Sha);
		await ApplyScopeOverlaysAsync();
		ResetChangeMap();
		CloseDocumentsExceptStart();
		CliLog.Write("action", $"commit scope {index + 1}/{ScopeCommits.Count} {commit.ShortSha}");
		ReviewChanged?.Invoke();
		CommitScopeChanged?.Invoke();
		OpenOverview();
		// The semantic workspaces stay on the review's head: they describe where the code
		// ends up, which is the right frame for navigating out of a commit being read.
		ComputeChangeMapAsync().HandleExceptions();
	}

	/// <summary>
	/// Points the semantic workspaces at the revision being displayed. They stay loaded
	/// for the review's head - reloading one per commit would mean a checkout and a
	/// solution load each step - so the files this commit touches are overlaid instead,
	/// which is what makes positions, symbols and occurrences agree with the text shown.
	/// </summary>
	async Task ApplyScopeOverlaysAsync()
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

	/// <summary>
	/// Forgets what was in scope. A review that has just been opened is the whole change by
	/// definition, and everything a scope holds belongs to the review it was entered from: the
	/// range it would return to, the commits it steps through, the tree it diffs against. Left
	/// behind, they describe a review that is no longer on screen, and the way out of a scope
	/// leads back to it.
	/// </summary>
	void ResetScope()
	{
		cachedCommitsRange = null;
		cachedCommits = [];
		cachedCommitStats = null;
		CommitScope = null;
		ScopeCommits = [];
		CommitScopeIndex = 0;
		SinceLastPassBase = null;
		ScopeLine = "";
		sinceLastPassTree = null;
		fullRange = null;
	}

	/// <summary>Back to reading the whole change at once, out of whichever scope was on.
	/// One exit for both: the button has always said "Whole change", and that is what it
	/// means whether a commit or the work since the last pass was being read.</summary>
	public async Task ExitScopeAsync()
	{
		if (fullRange is not { } range)
			return;
		CommitScope = null;
		SinceLastPassBase = null;
		ScopeLine = "";
		Semantics?.ClearTextOverlay();
		BaseSemantics?.ClearTextOverlay();
		BaseSha = range.Base;
		HeadSha = range.Head;
		fullRange = null;
		Files = DirtyWorktreePath is { } dirty
			? await Git.DiffWorkingTreeAsync(dirty, range.Base)
			: await Git.DiffAsync(range.Base, range.Head);
		IndexAddedLines(Files);
		if (CurrentPr is { } pr)
			Store.Open(Path.GetFileName(RepoPath), pr.Number, range.Head, range.Base);
		else
			// Keyed by the refs the review was opened with, exactly as OpenLocalRangeAsync
			// keyed it: a key built from SHAs instead names a state file nobody wrote, and
			// the review's own - its viewed flags, depth marks and drafts - is orphaned.
			Store.OpenLocal(Path.GetFileName(RepoPath), LocalRangeKey(range), range.Head, range.Base);
		ResetChangeMap();
		CloseDocumentsExceptStart();
		CliLog.Write("action", "scope off");
		ReviewChanged?.Invoke();
		CommitScopeChanged?.Invoke();
		OpenOverview();
		ComputeChangeMapAsync().HandleExceptions();
	}

	#endregion

	#region Reading only what changed since the last pass

	/// <summary>
	/// The tree the review is diffed against while only the work since the reader's last
	/// pass is in scope: everything they already read, replayed onto the current base. A
	/// tree and not a commit on purpose - after a rebase there is no commit whose diff to
	/// the head is the author's own edits, because the rebase mixed the new base into every
	/// one of them.
	/// </summary>
	public string? SinceLastPassBase { get; private set; }

	public bool InSinceLastPassScope => SinceLastPassBase is not null;

	/// <summary>True while the review is narrowed to anything less than the whole change.</summary>
	public bool InScope => CommitScope is not null || InSinceLastPassScope;

	/// <summary>What the reader is being shown, for the panes that head the file list. Empty
	/// when the whole change is in scope.</summary>
	public string ScopeLine { get; private set; } = "";

	public bool CanEnterSinceLastPassScope
		=> LastPassHead is not null && DirtyWorktreePath is null && ReviewRange is not null;

	/// <summary>The range whose commits are being read. The since-last-pass scope diffs
	/// against a synthetic tree, which has no history, so its commits are the ones written
	/// since the previous pass - however the rewrite arranged them.</summary>
	public (string Base, string Head)? CommitRange
		=> InSinceLastPassScope && LastPassHead is { } previous && HeadSha is { } head
			? (previous, head)
			: BaseSha is { } b && HeadSha is { } h ? (b, h) : null;

	/// <summary>The replay is a pure function of (base, last pass head), so it is computed
	/// once and kept for as long as the review is open.</summary>
	string? sinceLastPassTree;

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
	public async Task EnterSinceLastPassScopeAsync()
	{
		if (LastPassHead is not { } previous)
		{
			StatusMessage?.Invoke("No earlier pass is recorded for this review - Stampeded compares against the "
				+ "head you last opened it at, and this is the first.");
			return;
		}
		if (DirtyWorktreePath is not null)
		{
			StatusMessage?.Invoke("This review includes uncommitted work, which was never part of a pass; "
				+ "there is nothing to compare it against.");
			return;
		}
		if (InScope)
			await ExitScopeAsync();
		if (ReviewRange is not { } range)
			return;
		using var busy = Busy.Begin("Diffing against your last pass");
		if (sinceLastPassTree is null)
		{
			try
			{
				sinceLastPassTree = await Git.ReplayTreeAsync(range.Base, previous, LastPassBase);
			}
			catch (ToolFailedException ex)
			{
				StatusMessage?.Invoke($"Diff since last pass failed: {ex.Message}");
				return;
			}
		}
		if (sinceLastPassTree is null)
		{
			StatusMessage?.Invoke($"The work you read at {previous[..9]} does not replay onto {range.Base[..9]} "
				+ "without conflicts, so there is no clean diff of the author's edits alone. Showing the raw "
				+ "interdiff instead - it includes the commits the rebase brought in.");
			await OpenInterdiffAsync();
			return;
		}
		var files = await Git.DiffAsync(sinceLastPassTree, range.Head);
		if (files.Count == 0)
		{
			StatusMessage?.Invoke($"Nothing has changed since your last pass at {previous[..9]}"
				+ (await Git.IsAncestorAsync(previous, range.Head) ? "." : " - the branch was only rebased."));
			return;
		}
		bool rewritten = !await Git.IsAncestorAsync(previous, range.Head);
		// Counted while Files is still the whole change: a reader who works through a scope
		// where everything is ticked can otherwise approve a change they never read, and this
		// is what the review still owes them.
		int wholeChange = Files.Count;
		int neverViewed = Files.Count(f => !Store.IsViewed(f.Path));
		fullRange ??= (range.Base, range.Head);
		SinceLastPassBase = sinceLastPassTree;
		BaseSha = sinceLastPassTree;
		HeadSha = range.Head;
		Files = files;
		IndexAddedLines(Files);
		ScopeLine = $"Since your pass at {previous[..9]}{(rewritten ? " (head rewritten)" : "")}: "
			+ $"{files.Count} file(s). Whole change: {neverViewed} of {wholeChange} file(s) never viewed.";
		await ApplyScopeOverlaysAsync();
		ResetChangeMap();
		CloseDocumentsExceptStart();
		CliLog.Write("action", $"since-last-pass scope {previous[..9]} -> {range.Head[..9]} "
			+ $"({(rewritten ? "rewritten" : "fast-forward")}), base tree {sinceLastPassTree[..9]}, {files.Count} file(s)");
		StatusMessage?.Invoke(ScopeLine);
		ReviewChanged?.Invoke();
		CommitScopeChanged?.Invoke();
		OpenOverview();
		ComputeChangeMapAsync().HandleExceptions();
	}

	#endregion

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
		if (Drafts.Count > 0 && MainWindowOrNull() is { } owner)
		{
			int outdated = Drafts.Count(d => d.CurrentLine is null);
			bool close = await new ConfirmWindow("Close review",
				$"{Drafts.Count} draft comment(s) have not been submitted"
					+ (outdated > 0 ? $" ({outdated} outdated)" : "") + ".\n\n"
					+ "Closing keeps them: they are stored with the review and will be here when you open it again.",
				"Close review").ShowDialog<bool>(owner);
			if (!close)
			{
				PostStatus($"Review left open; {Drafts.Count} draft(s) still unsubmitted.");
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
		if (CurrentPr is { } pr)
			await OpenPrAsync(pr.Number);
		else if (LocalRange is { } local)
			await OpenLocalRangeAsync(local.Base, local.Head);
		else
			return;
		// A head that moved is reported by the carry-over, which knows what it kept; standing
		// still is the outcome nothing else would mention.
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
		LocalRange = null;
		ResetScope();
		DirtyWorktreePath = null;
		UncommittedFileCount = 0;
		BaseSha = null;
		HeadSha = null;
		Files = [];
		addedLinesByFile = [];
		removedLinesByFile = [];
		Coverage = null;
		Checks = null;
		PostedComments = [];
		CommentsLoaded = false;
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
		CommentsChanged?.Invoke();
		CliLog.Write("action", "review closed");
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
		if (Files.Count == 0)
			return;
		int index = 0;
		var current = CurrentFile;
		if (current is not null)
		{
			int i = Files.ToList().FindIndex(f => f.Path == current.Path);
			index = Math.Clamp(i + delta, 0, Files.Count - 1);
			if (i >= 0 && index == i)
				return;
		}
		if (await OpenFileAsync(Files[index]) is { } opened)
			FocusEditorOf(opened);
	}

	/// <summary>
	/// Puts keyboard focus in a document's editor once the dock has shown it. The single-key
	/// review gestures are handled by the editor, so advancing to the next file without this
	/// leaves them dead until the mouse is used: activating a dockable decides what is
	/// visible, not what the keyboard talks to.
	/// </summary>
	static void FocusEditorOf(DiffDocumentViewModel document)
		=> Avalonia.Threading.Dispatcher.UIThread.Post(
			() => global::Stampeded.Documents.DiffDocumentView.ViewFor(document)?.FocusEditor(),
			Avalonia.Threading.DispatcherPriority.Loaded);

	public async Task ToggleViewedAndAdvanceAsync()
	{
		var file = CurrentFile;
		if (file is null)
			return;
		bool viewed = !Store.IsViewed(file.Path);
		Store.SetViewed(file.Path, viewed);
		ViewedChanged?.Invoke(file.Path, viewed);
		if (!viewed)
			return;
		if (CommitScope is not null && Files.All(f => Store.IsViewed(f.Path)))
		{
			if (CommitScopeIndex + 1 < ScopeCommits.Count)
			{
				await StepCommitScopeAsync(1);
				return;
			}
			StatusMessage?.Invoke("Last commit read. 'Whole change' reviews the change as one diff.");
			return;
		}
		// The last file has nowhere to advance to, and the advance would silently do nothing -
		// leaving 'v' pressed once more to un-view the file just finished. The read is over at
		// that point, so it ends where the close-out lives.
		if (Files.Count > 0 && Files[^1].Path == file.Path)
		{
			OpenOverview();
			StatusMessage?.Invoke($"Last file read; {Files.Count(f => Store.IsViewed(f.Path))} of {Files.Count} viewed.");
			return;
		}
		await OpenAdjacentFileAsync(1);
	}

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
				// the first thing opened): the file to read is the first unread one.
				?? Files.FirstOrDefault(f => !Store.IsViewed(f.Path)) ?? Files.FirstOrDefault();
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
				!oldSide && addedLinesByFile.TryGetValue(x.Rel!, out var lines) && lines.Contains(x.Hit.Line),
				oldSide))
			.ToList();
		ReferencesAvailable?.Invoke(symbol.Name + (oldSide ? " (base)" : ""), items);
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

	/// <summary>Opens (or activates) a file at a blob line. Head side: the review diff when
	/// the file is in the PR, else a head source view. Base side: the review diff mapped via
	/// the old line, else a base source view.</summary>
	public async Task NavigateToFileLineAsync(string relPath, int fileLine, bool oldSide, bool record)
	{
		DiffDocumentViewModel? vm;
		int docLine;
		var fileDiff = oldSide
			? Files.FirstOrDefault(f => f.OldPath == relPath)
			: Files.FirstOrDefault(f => f.Path == relPath);
		if (fileDiff is not null)
		{
			vm = await OpenFileAsync(fileDiff);
			docLine = (oldSide ? vm?.Model.DocLineFromOldLine(fileLine) : vm?.Model.DocLineFromNewLine(fileLine)) ?? fileLine;
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
			docLine = fileLine;
		}
		if (vm is null)
			return;
		if (record)
			history.Record(new NavEntry(vm.Id, fileLine, oldSide));
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
			var headMembers = addedLinesByFile.TryGetValue(file.Path, out var added) && added.Count > 0
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
			if (baseSem is not null
				&& removedLinesByFile.TryGetValue(file.OldPath, out var removed) && removed.Count > 0)
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

	#region Review comments

	public sealed record DraftComment(StoredComment Stored, int? CurrentLine, bool IsApproximate = false);

	/// <summary><paramref name="InReplyTo"/> names the posted comment this one answers, when
	/// the reader is replying rather than starting a thread.</summary>
	public sealed record CommentTarget(string RelPath, bool OldSide, int Line, string LineText, long? InReplyTo = null);

	public sealed record PostedCommentView(string RelPath, int? Line, bool OldSide, string Body, string Author,
		bool IsApproximate = false, string? ThreadId = null, bool IsResolved = false, string? Url = null,
		long CommentId = 0);

	public IReadOnlyList<DraftComment> Drafts { get; private set; } = [];
	public IReadOnlyList<PostedCommentView> PostedComments { get; private set; } = [];

	/// <summary>True once the posted-comments fetch for the current review finished
	/// (successfully or not) - distinguishes "none" from "still loading".</summary>
	public bool CommentsLoaded { get; private set; }
	public CommentTarget? PendingCommentTarget { get; private set; }

	public event Action? CommentsChanged;
	public event Action? CommentTargetRequested;

	/// <summary>Set by the dock factory so 'comment here' can surface the Comments pane.</summary>
	public Dock.Model.Core.IDockable? CommentsPane { get; set; }

	/// <summary>Review comments live on a pull request. A local-branch or uncommitted-work
	/// review has nowhere to post them, so drafting one would only ever produce a draft that
	/// can never leave the machine.</summary>
	public bool CanComment => CurrentPr is not null;

	public void BeginComment(CommentTarget target, bool activatePane = true)
	{
		if (!CanComment)
		{
			PostStatus("Comments need a pull request; this is a local review.");
			return;
		}
		if (target.OldSide && InSinceLastPassScope)
		{
			// The left side here is the reader's last pass replayed onto the current base,
			// not the pull request's base. GitHub reads a LEFT line against the latter, so
			// this comment would be posted against a line nobody wrote.
			PostStatus("The left side of this scope is your last pass, not the pull request's base, so a "
				+ "comment there has no line to land on. Press 'Whole change' to comment on removed code.");
			return;
		}
		PendingCommentTarget = target;
		if (activatePane && CommentsPane is not null && Factory is not null)
			Factory.SetActiveDockable(CommentsPane);
		CommentTargetRequested?.Invoke();
	}

	public async Task CommitDraftAsync(string body)
	{
		if (PendingCommentTarget is not { } target || body.Length == 0)
			return;
		string rev = target.OldSide ? BaseSha! : HeadSha!;
		var lines = SplitBlobLines(await Git.ShowFileAsync(rev, target.RelPath));
		if (target.Line < 1 || target.Line > lines.Length)
			return;
		var anchor = CommentAnchor.Create(target.RelPath, target.OldSide, target.Line, lines);
		Store.AddDraft(new StoredComment(Guid.NewGuid(), anchor, body, DateTimeOffset.Now, target.InReplyTo));
		PendingCommentTarget = null;
		RebuildDrafts();
	}

	public void UpdateDraft(Guid id, string body)
	{
		if (body.Trim().Length == 0)
			return;
		Store.UpdateDraft(id, body);
		RebuildDrafts();
	}

	public void RemoveDraft(Guid id)
	{
		Store.RemoveDraft(id);
		RebuildDrafts();
	}

	void RebuildDrafts()
	{
		Drafts = Store.Drafts.Select(d => new DraftComment(d, d.Anchor.Line)).ToList();
		CommentsChanged?.Invoke();
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
	async Task ReattachDraftsAsync(CancellationToken ct)
	{
		var reattached = new List<DraftComment>();
		foreach (var stored in Store.Drafts)
		{
			int? line = null;
			bool approximate = false;
			try
			{
				string rev = stored.Anchor.OldSide ? BaseSha! : HeadSha!;
				var lines = SplitBlobLines(await Git.ShowFileAsync(rev, stored.Anchor.Path, ct));
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
		CommentsChanged?.Invoke();
	}

	/// <summary>
	/// Re-reads the posted comments from GitHub. They are fetched when the review opens and
	/// after submitting, so anything said meanwhile - a reply, a resolved thread, a review
	/// from someone else - is invisible until asked for.
	/// </summary>
	public async Task RefreshPostedCommentsAsync()
	{
		if (CurrentPr is not { } pr)
		{
			PostStatus("No pull request: a local review has no posted comments to fetch.");
			return;
		}
		using var busy = Busy.Begin("Refreshing comments");
		await LoadPostedCommentsAsync(pr.Number, CancellationToken.None);
	}

	async Task LoadPostedCommentsAsync(int number, CancellationToken ct)
	{
		CommentsLoaded = false;
		try
		{
			var raw = await GitHub.GetReviewCommentsAsync(number, ct);
			Dictionary<long, (string ThreadId, bool Resolved)> resolutionByComment = [];
			try
			{
				foreach (var thread in await GitHub.GetThreadResolutionsAsync(number, ct))
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
				bool approximate = false;
				int? line = comment.Line ?? await ReanchorPostedAsync(comment, oldSide, ct);
				if (line is null)
				{
					line = await ApproximatePostedAsync(comment, oldSide, ct);
					approximate = line is not null;
				}
				var resolution = resolutionByComment.GetValueOrDefault(comment.Id);
				views.Add(new PostedCommentView(
					comment.Path, line, oldSide, comment.Body, comment.User?.Login ?? "?",
					approximate, resolution.ThreadId, resolution.Resolved, comment.HtmlUrl, comment.Id));
			}
			PostedComments = views;
		}
		catch (ToolFailedException)
		{
			PostedComments = [];
		}
		CommentsLoaded = true;
		CommentsChanged?.Invoke();
	}

	/// <summary>GitHub nulls `line` once the diff moved on; the comment's diff_hunk
	/// excerpt ends at the commented line, so re-anchor it by content (line text plus up
	/// to two same-side context lines) against the current blob, like drafts.</summary>
	async Task<int?> ReanchorPostedAsync(PostedComment comment, bool oldSide, CancellationToken ct)
	{
		if (comment.DiffHunk is null || comment.OriginalLine is null)
			return null;
		string? rev = oldSide ? BaseSha : HeadSha;
		if (rev is null)
			return null;
		var sideLines = new List<string>();
		foreach (var hunkLine in comment.DiffHunk.ReplaceLineEndings("\n").Split('\n'))
		{
			if (hunkLine.StartsWith("@@", StringComparison.Ordinal))
				continue;
			char marker = hunkLine.Length > 0 ? hunkLine[0] : ' ';
			if (marker == ' ' || (oldSide ? marker == '-' : marker == '+'))
				sideLines.Add(hunkLine.Length > 0 ? hunkLine[1..] : "");
		}
		if (sideLines.Count == 0)
			return null;
		string lineText = sideLines[^1];
		var before = sideLines.Count > 1
			? sideLines.GetRange(Math.Max(0, sideLines.Count - 3), Math.Min(2, sideLines.Count - 1))
			: [];
		var anchor = new CommentAnchor(comment.Path, oldSide, comment.OriginalLine.Value, lineText, before, []);
		try
		{
			var blobLines = SplitBlobLines(await Git.ShowFileAsync(rev, comment.Path, ct));
			return anchor.Reattach(blobLines);
		}
		catch (ToolFailedException)
		{
			return null;
		}
	}

	/// <summary>Approximate location for a posted comment whose exact spot is gone: the
	/// synthetic hunk-tail anchor's context-based best match in the current blob.</summary>
	async Task<int?> ApproximatePostedAsync(PostedComment comment, bool oldSide, CancellationToken ct)
	{
		if (comment.DiffHunk is null || comment.OriginalLine is null)
			return null;
		string? rev = oldSide ? BaseSha : HeadSha;
		if (rev is null)
			return null;
		var sideLines = new List<string>();
		foreach (var hunkLine in comment.DiffHunk.ReplaceLineEndings("\n").Split('\n'))
		{
			if (hunkLine.StartsWith("@@", StringComparison.Ordinal))
				continue;
			char marker = hunkLine.Length > 0 ? hunkLine[0] : ' ';
			if (marker == ' ' || (oldSide ? marker == '-' : marker == '+'))
				sideLines.Add(hunkLine.Length > 0 ? hunkLine[1..] : "");
		}
		if (sideLines.Count == 0)
			return null;
		var before = sideLines.Count > 1
			? sideLines.GetRange(Math.Max(0, sideLines.Count - 3), Math.Min(2, sideLines.Count - 1))
			: [];
		var anchor = new CommentAnchor(comment.Path, oldSide, comment.OriginalLine.Value, sideLines[^1], before, []);
		try
		{
			var blobLines = SplitBlobLines(await Git.ShowFileAsync(rev, comment.Path, ct));
			return anchor.Approximate(blobLines);
		}
		catch (ToolFailedException)
		{
			return null;
		}
	}

	/// <summary>Sets a review thread's resolution on GitHub, then refreshes the comments.</summary>
	public async Task SetThreadResolvedAsync(string threadId, bool resolved)
	{
		if (CurrentPr is not { } pr)
			return;
		try
		{
			await GitHub.SetThreadResolvedAsync(threadId, resolved);
			await LoadPostedCommentsAsync(pr.Number, CancellationToken.None);
		}
		catch (ToolFailedException ex)
		{
			StatusMessage?.Invoke($"Thread resolution failed: {ex.Message}");
		}
	}

	/// <summary>Lines each side of a file that GitHub accepts review comments on (lines
	/// that appear in the diff hunks).</summary>
	(HashSet<int> NewLines, HashSet<int> OldLines) CommentableLines(FileDiff file)
	{
		var newLines = new HashSet<int>();
		var oldLines = new HashSet<int>();
		foreach (var hunk in file.Hunks)
		{
			int newLine = hunk.NewStart, oldLine = hunk.OldStart;
			foreach (var line in hunk.Lines)
			{
				if (line.Kind != PatchLineKind.Removed)
					newLines.Add(newLine++);
				if (line.Kind != PatchLineKind.Added)
					oldLines.Add(oldLine++);
			}
		}
		return (newLines, oldLines);
	}

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

	/// <summary>Whether the open review is of the user's own pull request. GitHub rejects
	/// APPROVE and REQUEST_CHANGES on those, so only a plain comment review can be
	/// submitted. False when nothing is open, or when gh cannot say who it is - the
	/// submission itself is the real gate, this only keeps the UI from offering what would
	/// certainly fail.</summary>
	public async Task<bool> IsOwnPullRequestAsync()
	{
		if (CurrentPr?.Author?.Login is not { Length: > 0 } author)
			return false;
		try
		{
			return string.Equals(author, await GitHub.GetViewerLoginAsync(), StringComparison.OrdinalIgnoreCase);
		}
		catch (ToolFailedException)
		{
			return false;
		}
	}

	/// <summary>Submits drafts that sit on commentable diff lines as a review; drafts that
	/// don't (outdated or outside the diff) stay local. Returns (submitted, skipped).</summary>
	/// <summary>
	/// Submits a review after the two checks that can refuse one, and reports what happened in
	/// a line meant for a reader. Both places that submit - the Comments pane and the review
	/// view - go through this, so a verdict cannot be refused in one and slip through the other.
	/// </summary>
	public async Task<string> SubmitReviewCheckedAsync(string eventType, string body)
	{
		if (eventType == "APPROVE" && ApprovalGate?.Invoke() is { Ok: false } gate)
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
			var (submitted, skipped) = await SubmitReviewAsync(eventType, body);
			return $"Review submitted ({eventType}): {submitted} comment(s) posted"
				+ (skipped > 0 ? $", {skipped} kept local (outdated/off-diff)" : "") + ".";
		}
		catch (ToolFailedException ex)
		{
			return ex.Message;
		}
	}

	/// <summary>
	/// Merges the current pull request after asking, and says what happened either way.
	/// The question names the branches and the method because this is the one command in the
	/// tool that changes what everyone else sees and cannot be taken back from here.
	/// </summary>
	public async Task<string> MergeCurrentPrAsync(string method)
	{
		if (CurrentPr is not { } pr)
			return "No pull request is open.";
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

	public async Task<(int Submitted, int Skipped)> SubmitReviewAsync(string eventType, string body)
	{
		if (CurrentPr is not { } pr)
			return (0, 0);
		if (InScope)
		{
			// Drafts are matched against the files in scope, so submitting from one would
			// keep every draft written elsewhere local and report it as outdated - which is
			// not what happened to it.
			StatusMessage?.Invoke($"A review is submitted for the whole change; press 'Whole change' first. "
				+ $"Your {Drafts.Count} draft(s) are kept.");
			return (0, Drafts.Count);
		}
		var commentable = Files.ToDictionary(f => f, CommentableLines);
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
				? Files.FirstOrDefault(f => f.OldPath == anchor.Path)
				: Files.FirstOrDefault(f => f.Path == anchor.Path);
			// A generated file has no counterpart in the pull request, so GitHub would reject
			// the whole review over it. Such a draft stays local, like an outdated one.
			bool ok = draft.CurrentLine is { } line && file is { IsGenerated: false }
				&& (anchor.OldSide
					? commentable[file].OldLines.Contains(line)
					: commentable[file].NewLines.Contains(line));
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
			await GitHub.SubmitReviewAsync(pr.Number, new ReviewSubmission(body, eventType, payload));
		for (int i = 0; i < replies.Count; i++)
		{
			var (inReplyTo, replyBody, id) = replies[i];
			await GitHub.ReplyToCommentAsync(pr.Number, inReplyTo,
				!reviewSubmitted && i == 0 ? GitHubService.AttributedReply(replyBody) : replyBody);
			submitted.Add(id);
		}
		foreach (var id in submitted)
			Store.RemoveDraft(id);
		RebuildDrafts();
		await LoadPostedCommentsAsync(pr.Number, CancellationToken.None);
		return (submitted.Count, skipped);
	}

	#endregion
}
