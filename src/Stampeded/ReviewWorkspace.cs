using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Decompilation;
using Stampeded.Core.Diff;
using Stampeded.Core.Git;
using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;
using Stampeded.Core.Review;
using Stampeded.Core.Roslyn;
using Stampeded.Documents;
using Stampeded.Navigation;

namespace Stampeded;

sealed record NavEntry(string DockableId, int DocLine) : IEquatable<NavEntry?>;

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
	public ReviewStateStore Store { get; } = new();
	public BusyTracker Busy { get; } = new();

	// Set by MainViewModel once the layout exists.
	public Docking.StampededDockFactory? Factory { get; set; }
	public DocumentDock? Documents { get; set; }

	public PrDetail? CurrentPr { get; private set; }
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
		foreach (var entry in ChangeMap.Where(e => !e.OldSide).Take(30))
		{
			var symbol = await SymbolAtAsync(oldSide: false, entry.RelPath, entry.Line, 1)
				?? await SymbolAtAsync(oldSide: false, entry.RelPath, entry.Line, 20);
			if (symbol is null)
				continue;
			var hits = await sem.FindReferencesAsync(symbol, CancellationToken.None);
			foreach (var hit in hits)
			{
				string? rel = sem.ToRelativePath(hit.FilePath);
				if (rel is null || !Core.Review.TestPaths.IsTestPath(rel))
					continue;
				classes.Add(Path.GetFileNameWithoutExtension(hit.FilePath));
				if (classes.Count >= 8)
					return classes.ToList();
			}
		}
		return classes.ToList();
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
	public string? WorktreePath { get; private set; }
	public string? BaseWorktreePath { get; private set; }

	public event Action? ReviewChanged;
	public event Action<string, bool>? ViewedChanged;
	public event Action? SemanticsChanged;
	public event Action? CoverageChanged;
	public event Action? ChecksLoaded;
	public event Action<string, string>? PickaxeRequested;

	public void RequestPickaxe(string text, string path) => PickaxeRequested?.Invoke(text, path);
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
		var files = await Git.DiffAsync(baseSha, headSha, ct);
		ct.ThrowIfCancellationRequested();

		CurrentPr = null;
		BaseSha = baseSha;
		HeadSha = headSha;
		Files = files;
		IndexAddedLines(files);
		Store.OpenLocal(Path.GetFileName(RepoPath), $"{baseRef}..{headRef}", headSha);
		await ApplyReReviewCarryOverAsync(ct);
		ComputeChurnAsync().HandleExceptions();
		history.Clear();
		CloseDocumentsExceptStart();
		PostedComments = [];
		CommentsLoaded = true;
		ReviewChanged?.Invoke();
		// Overview first so it holds the leftmost tab; the diff tabs open behind it and
		// the continuation brings it back to the front.
		OpenOverview();
		OpenUnviewedFilesAsync()
			.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => {
				OpenOverview();
				CloseStartPage();
			}))
			.HandleExceptions();
		LoadSemanticsAsync(headSha, baseSha, ct).HandleExceptions();
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
		var files = await Git.DiffAsync(baseSha, headSha, ct);
		ct.ThrowIfCancellationRequested();

		CurrentPr = detail;
		BaseSha = baseSha;
		HeadSha = headSha;
		Files = files;
		IndexAddedLines(files);
		Store.Open(Path.GetFileName(RepoPath), number, headSha);
		await ApplyReReviewCarryOverAsync(ct);
		ComputeChurnAsync().HandleExceptions();
		history.Clear();
		CloseDocumentsExceptStart();
		ReviewChanged?.Invoke();
		// Overview first so it holds the leftmost tab; the diff tabs open behind it and
		// the continuation brings it back to the front.
		OpenOverview();
		OpenUnviewedFilesAsync()
			.ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => {
				OpenOverview();
				CloseStartPage();
			}))
			.HandleExceptions();
		LoadSemanticsAsync(headSha, baseSha, ct).HandleExceptions();
		ReattachDraftsAsync(ct).HandleExceptions();
		LoadPostedCommentsAsync(number, ct).HandleExceptions();
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
		// The base-side workspace powers navigation FROM removed lines; load it after the
		// head side so the common case is interactive first.
		var baseSemantics = BaseSemantics = new RoslynWorkspaceService();
		baseSemantics.StateChanged += () => SemanticsChanged?.Invoke();
		using (Busy.Begin("Loading semantics (base)"))
		{
			BaseWorktreePath = await Worktrees.GetOrCreateAsync(baseSha, ct);
			await Task.Run(() => baseSemantics.LoadAsync(BaseWorktreePath, ct), ct);
		}
		using (Busy.Begin("Computing change map"))
			await ComputeChangeMapAsync();
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

	/// <summary>Opens every not-yet-viewed file as a diff tab and focuses the first, so a
	/// fresh review starts with the whole remaining work queue in front of the reviewer.</summary>
	async Task OpenUnviewedFilesAsync()
	{
		DiffDocumentViewModel? first = null;
		// Likely review order: tests first (the executable spec), then implementation,
		// deep-marked files ahead within each group.
		var ordered = Files
			.OrderBy(f => Core.Review.TestPaths.IsTestPath(f.Path) ? 0 : 1)
			.ThenBy(f => GetDepth(f.Path) == "deep" ? 0 : 1)
			.ThenBy(f => f.Path, StringComparer.Ordinal);
		foreach (var file in ordered)
		{
			if (Store.IsViewed(file.Path))
				continue;
			var vm = await OpenFileAsync(file);
			first ??= vm;
		}
		if (first is not null && Factory is not null && Documents is not null)
		{
			Factory.SetActiveDockable(first);
			Factory.SetFocusedDockable(Documents, first);
		}
	}

	public async Task<DiffDocumentViewModel?> OpenFileAsync(FileDiff file)
	{
		if (BaseSha is null || HeadSha is null)
			return null;
		string oldText = file.Kind == FileChangeKind.Added || file.IsBinary
			? ""
			: await Git.ShowFileAsync(BaseSha, file.OldPath);
		string newText = file.Kind == FileChangeKind.Deleted || file.IsBinary
			? ""
			: await Git.ShowFileAsync(HeadSha, file.NewPath);
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

	/// <summary>Head of the previous pass, when this open superseded one; null on a first
	/// pass or when the previous head's objects are gone.</summary>
	public string? LastPassHead { get; private set; }

	/// <summary>Files the interdiff (last pass head -> current head) touched.</summary>
	public IReadOnlySet<string>? TouchedSinceLastPass { get; private set; }

	public bool IsTouchedSinceLastPass(string path) => TouchedSinceLastPass?.Contains(path) ?? false;

	/// <summary>Re-review is not a repeat: viewed flags carry over except for files the new
	/// push touched, so the unviewed set - which drives which files open - becomes exactly
	/// "invalidated plus never seen".</summary>
	async Task ApplyReReviewCarryOverAsync(CancellationToken ct)
	{
		LastPassHead = null;
		TouchedSinceLastPass = null;
		if (Store.Superseded is not { } superseded || HeadSha is null)
			return;
		try
		{
			var changes = await Git.DiffNameStatusAsync(superseded.PreviousHead, HeadSha, ct);
			var touched = changes.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
			LastPassHead = superseded.PreviousHead;
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

	/// <summary>The raw interdiff (last pass head -> current head) as a document.</summary>
	public async Task OpenInterdiffAsync()
	{
		if (LastPassHead is null || HeadSha is null)
		{
			StatusMessage?.Invoke("No earlier pass recorded for this head - the interdiff needs a previous review at another head.");
			return;
		}
		try
		{
			string patch = await ExternalTool.RunAsync("git", ["diff", LastPassHead, HeadSha], RepoPath);
			OpenTextDocument($"interdiff:{LastPassHead[..9]}", $"interdiff {LastPassHead[..9]}..{HeadSha[..9]}", patch);
		}
		catch (ToolFailedException ex)
		{
			StatusMessage?.Invoke($"Interdiff failed: {ex.Message}");
		}
	}

	#endregion

	#region Review phases: triage / sweep / record

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

	public sealed record SweepItem(string Title, string? Path, int Line);

	public IReadOnlyList<SweepItem>? LastSweep { get; private set; }

	/// <summary>The delocalized-consequence checklist, answered mechanically where possible.
	/// These are prompts to a human, not verdicts - noise is acceptable, silence is not.</summary>
	public async Task<IReadOnlyList<SweepItem>> ComputeSweepAsync()
	{
		var items = new List<SweepItem>();

		// Removed members whose name still appears in the head worktree (surviving callers).
		var removedNames = ChangeMap
			.Where(e => e.Kind == "Removed")
			.Select(e => MemberSimpleName(e.Display))
			.Where(n => n.Length >= 3)
			.Distinct()
			.Take(20)
			.ToList();
		foreach (var name in removedNames)
		{
			if (WorktreePath is null)
				break;
			try
			{
				string hits = await ExternalTool.RunAsync("git", ["grep", "-n", "-w", "--", name], WorktreePath);
				var first = hits.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
				int count = hits.Count(c => c == '\n');
				if (first is not null)
				{
					var parts = first.Split(':', 3);
					int.TryParse(parts.ElementAtOrDefault(1), out int line);
					items.Add(new SweepItem($"Removed '{name}' still mentioned {count + 1}x at head - surviving caller?",
						parts[0], Math.Max(1, line)));
				}
			}
			catch (ToolFailedException)
			{
				// exit 1 = no hits: the removal is clean.
			}
		}

		foreach (var file in Files.Where(f => Core.Review.TriageEstimate.IsDependencyFile(f.Path)))
			items.Add(new SweepItem($"Dependency/manifest change: {file.Path}", file.Path, 1));

		bool testsTouched = Files.Any(f => Core.Review.TestPaths.IsTestPath(f.Path));
		int nonTestMembers = ChangeMap.Count(e => !Core.Review.TestPaths.IsTestPath(e.RelPath));
		if (nonTestMembers > 0 && !testsTouched)
			items.Add(new SweepItem($"{nonTestMembers} changed member(s) but NO test file touched", null, 0));

		foreach (var file in Files)
		{
			int newLine = 0;
			foreach (var hunk in file.Hunks)
			{
				newLine = hunk.NewStart;
				foreach (var line in hunk.Lines)
				{
					if (line.Kind == PatchLineKind.Added
						&& (line.Text.Contains("TODO") || line.Text.Contains("FIXME") || line.Text.Contains("HACK")))
					{
						items.Add(new SweepItem($"Added TODO/FIXME in {file.Path}", file.Path, newLine));
					}
					if (line.Kind != PatchLineKind.Removed)
						newLine++;
				}
			}
		}

		var (uncovered, measured) = UncoveredAddedLines();
		if (Coverage is null)
			items.Add(new SweepItem("No coverage run - added lines unverified (Tests pane > Run + Coverage)", null, 0));
		else if (uncovered > 0)
			items.Add(new SweepItem($"{uncovered} of {measured} measured added line(s) uncovered", null, 0));

		LastSweep = items;
		return items;
	}

	static string MemberSimpleName(string display)
	{
		int paren = display.IndexOf('(');
		string noArgs = paren < 0 ? display : display[..paren];
		int dot = noArgs.LastIndexOf('.');
		return dot < 0 ? noArgs : noArgs[(dot + 1)..];
	}

	/// <summary>The honest close-out artifact: what was reviewed at what depth, what was
	/// verified, and what was deliberately not looked at.</summary>
	public void OpenReviewRecord()
	{
		var text = new System.Text.StringBuilder();
		text.AppendLine(CurrentPr is { } pr ? $"Review record: #{pr.Number} {pr.Title}" : "Review record");
		text.AppendLine($"Head: {HeadSha}");
		text.AppendLine();
		foreach (var group in Files.GroupBy(f => GetDepth(f.Path) is "" ? "(unplanned)" : GetDepth(f.Path)).OrderBy(g => g.Key))
		{
			text.AppendLine($"---- {group.Key} ----");
			foreach (var file in group)
				text.AppendLine($"  {(Store.IsViewed(file.Path) ? "viewed " : "NOT viewed")}  {file.Path}");
		}
		text.AppendLine();
		var (uncovered, measured) = UncoveredAddedLines();
		text.AppendLine(Coverage is null
			? "Coverage: not measured."
			: $"Coverage: {uncovered} uncovered of {measured} measured added line(s).");
		text.AppendLine($"Sweep findings: {(LastSweep is null ? "sweep not run" : $"{LastSweep.Count} item(s)")}");
		text.AppendLine($"Draft comments pending: {Drafts.Count}");
		text.AppendLine();
		text.AppendLine("Not reviewed (trust or unviewed):");
		foreach (var file in Files.Where(f => GetDepth(f.Path) == "trust" || !Store.IsViewed(f.Path)))
			text.AppendLine($"  {file.Path}");
		OpenTextDocument($"record:{CurrentPr?.Number ?? 0}", "Review record", text.ToString());
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
				: await Git.ShowFileAsync(HeadSha, file.NewPath);
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
		var keep = new List<string>();
		if (BaseSha is not null)
			keep.Add(BaseSha);
		if (HeadSha is not null)
			keep.Add(HeadSha);
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
				OpenTextDocument($"show:{sha}", $"commit {sha[..9]}", patch);
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

	/// <summary>Stops background work and releases the Roslyn workspaces; called when the
	/// app switches to another repository and this instance is abandoned.</summary>
	public void Shutdown()
	{
		sessionCts?.Cancel();
		Semantics?.Dispose();
		BaseSemantics?.Dispose();
	}

	/// <summary>Opens any URL in the browser (Linux-first: xdg-open).</summary>
	public Task OpenUrlAsync(string url)
		=> ExternalTool.RunAsync("xdg-open", [url], RepoPath);

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
		string? root = oldSide ? BaseWorktreePath : WorktreePath;
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
	public void CloseReview()
	{
		sessionCts?.Cancel();
		Semantics?.Dispose();
		Semantics = null;
		BaseSemantics?.Dispose();
		BaseSemantics = null;
		CurrentPr = null;
		BaseSha = null;
		HeadSha = null;
		Files = [];
		addedLinesByFile = [];
		removedLinesByFile = [];
		Coverage = null;
		Checks = null;
		LastSweep = null;
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
		=> ShowDocument("overview", () => new Documents.OverviewDocumentViewModel(this));

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
		await OpenFileAsync(Files[index]);
	}

	public async Task ToggleViewedAndAdvanceAsync()
	{
		var file = CurrentFile;
		if (file is null)
			return;
		bool viewed = !Store.IsViewed(file.Path);
		Store.SetViewed(file.Path, viewed);
		ViewedChanged?.Invoke(file.Path, viewed);
		if (viewed)
			await OpenAdjacentFileAsync(1);
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

	async Task<Microsoft.CodeAnalysis.ISymbol?> SymbolAtAsync(bool oldSide, string relPath, int line, int column)
	{
		var sem = SemanticsFor(oldSide);
		if (!IsReady(sem))
			return null;
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
			history.Record(new NavEntry(id, result.MemberLine));
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
			string? root = oldSide ? BaseWorktreePath : WorktreePath;
			if (root is null)
				return;
			string absolute = Path.Combine(root, relPath);
			if (!File.Exists(absolute))
				return;
			string prefix = oldSide ? "srcbase:" : "src:";
			vm = ShowDocument(prefix + relPath, () => {
				string text = File.ReadAllText(absolute);
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
			history.Record(new NavEntry(vm.Id, docLine));
		vm.RequestCaret(docLine);
	}

	public readonly record struct NavEntryOrigin(string DockableId, int DocLine);

	void RecordOrigin(NavEntryOrigin origin)
		=> history.Record(new NavEntry(origin.DockableId, origin.DocLine));

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
				doc.RequestCaret(entry.DocLine);
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
				vm?.RequestCaret(entry.DocLine);
				return;
			}
		}
		bool oldSide = entry.DockableId.StartsWith("srcbase:", StringComparison.Ordinal);
		string? root = oldSide ? BaseWorktreePath : WorktreePath;
		if (root is null)
			return;
		string absolute = Path.Combine(root, relPath);
		if (!File.Exists(absolute))
			return;
		var source = ShowDocument(entry.DockableId, () => {
			var vm = DiffDocumentViewModel.ForSource(relPath, File.ReadAllText(absolute));
			if (oldSide)
				vm.Title += " @ base";
			return vm;
		});
		source?.RequestCaret(entry.DocLine);
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

	public sealed record CommentTarget(string RelPath, bool OldSide, int Line, string LineText);

	public sealed record PostedCommentView(string RelPath, int? Line, bool OldSide, string Body, string Author,
		bool IsApproximate = false, string? ThreadId = null, bool IsResolved = false, string? Url = null);

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

	public void BeginComment(CommentTarget target, bool activatePane = true)
	{
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
		Store.AddDraft(new StoredComment(Guid.NewGuid(), anchor, body, DateTimeOffset.Now));
		PendingCommentTarget = null;
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
					approximate, resolution.ThreadId, resolution.Resolved, comment.HtmlUrl));
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

	/// <summary>Submits drafts that sit on commentable diff lines as a review; drafts that
	/// don't (outdated or outside the diff) stay local. Returns (submitted, skipped).</summary>
	public async Task<(int Submitted, int Skipped)> SubmitReviewAsync(string eventType, string body)
	{
		if (CurrentPr is not { } pr)
			return (0, 0);
		var commentable = Files.ToDictionary(f => f, CommentableLines);
		var payload = new List<ReviewCommentDto>();
		var submitted = new List<Guid>();
		int skipped = 0;
		foreach (var draft in Drafts)
		{
			var anchor = draft.Stored.Anchor;
			var file = anchor.OldSide
				? Files.FirstOrDefault(f => f.OldPath == anchor.Path)
				: Files.FirstOrDefault(f => f.Path == anchor.Path);
			bool ok = draft.CurrentLine is { } line && file is not null
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
		await GitHub.SubmitReviewAsync(pr.Number, new ReviewSubmission(body, eventType, payload));
		foreach (var id in submitted)
			Store.RemoveDraft(id);
		RebuildDrafts();
		await LoadPostedCommentsAsync(pr.Number, CancellationToken.None);
		return (submitted.Count, skipped);
	}

	#endregion
}
