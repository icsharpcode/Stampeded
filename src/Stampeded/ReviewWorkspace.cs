using Dock.Model.Mvvm.Controls;

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
	public event Action<string, IReadOnlyList<ReferenceItem>>? ReferencesAvailable;

	CancellationTokenSource? sessionCts;
	Dictionary<string, HashSet<int>> addedLinesByFile = [];
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
		history.Clear();
		CloseOpenDiffs();
		PostedComments = [];
		ReviewChanged?.Invoke();
		OpenUnviewedFilesAsync().HandleExceptions();
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
		history.Clear();
		CloseOpenDiffs();
		ReviewChanged?.Invoke();
		OpenUnviewedFilesAsync().HandleExceptions();
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
	}

	void IndexAddedLines(IReadOnlyList<FileDiff> files)
	{
		addedLinesByFile = [];
		foreach (var file in files)
		{
			var lines = new HashSet<int>();
			foreach (var hunk in file.Hunks)
			{
				int newLine = hunk.NewStart;
				foreach (var line in hunk.Lines)
				{
					if (line.Kind == PatchLineKind.Added)
						lines.Add(newLine);
					if (line.Kind != PatchLineKind.Removed)
						newLine++;
				}
			}
			addedLinesByFile[file.Path] = lines;
		}
	}

	void CloseOpenDiffs()
	{
		if (Documents?.VisibleDockables is null || Factory is null)
			return;
		foreach (var diff in Documents.VisibleDockables.OfType<DiffDocumentViewModel>().ToList())
			Factory.CloseDockable(diff);
	}

	/// <summary>Opens every not-yet-viewed file as a diff tab and focuses the first, so a
	/// fresh review starts with the whole remaining work queue in front of the reviewer.</summary>
	async Task OpenUnviewedFilesAsync()
	{
		DiffDocumentViewModel? first = null;
		foreach (var file in Files)
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

	DiffDocumentViewModel? ShowDocument(string id, Func<DiffDocumentViewModel> create)
	{
		if (Documents is null || Factory is null)
			return null;
		var existing = Documents.VisibleDockables?
			.OfType<DiffDocumentViewModel>()
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

	/// <summary>Opens a PR in the browser via gh.</summary>
	public Task OpenOnGitHubAsync(int number)
		=> ExternalTool.RunAsync("gh", ["pr", "view", number.ToString(), "--web"], RepoPath);

	/// <summary>Opens (or activates) a plain text document tab (CI logs, reports, ...).</summary>
	public void OpenTextDocument(string id, string title, string text)
	{
		ShowDocument(id, () => {
			var vm = DiffDocumentViewModel.ForSource(title, text);
			vm.Title = title;
			return vm;
		});
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
			return;
		string? targetRel = sem.ToRelativePath(location.FilePath);
		if (targetRel is null)
			return;
		CliLog.Write("action", $"goto definition: {targetRel}:{location.Line}{(oldSide ? " (base)" : "")}");
		RecordOrigin(origin);
		await NavigateToFileLineAsync(targetRel, location.Line, oldSide, record: true);
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

	#region Review comments

	public sealed record DraftComment(StoredComment Stored, int? CurrentLine);

	public sealed record CommentTarget(string RelPath, bool OldSide, int Line, string LineText);

	public sealed record PostedCommentView(string RelPath, int? Line, bool OldSide, string Body, string Author);

	public IReadOnlyList<DraftComment> Drafts { get; private set; } = [];
	public IReadOnlyList<PostedCommentView> PostedComments { get; private set; } = [];
	public CommentTarget? PendingCommentTarget { get; private set; }

	public event Action? CommentsChanged;
	public event Action? CommentTargetRequested;

	/// <summary>Set by the dock factory so 'comment here' can surface the Comments pane.</summary>
	public Dock.Model.Core.IDockable? CommentsPane { get; set; }

	public void BeginComment(CommentTarget target)
	{
		PendingCommentTarget = target;
		if (CommentsPane is not null && Factory is not null)
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
			try
			{
				string rev = stored.Anchor.OldSide ? BaseSha! : HeadSha!;
				var lines = SplitBlobLines(await Git.ShowFileAsync(rev, stored.Anchor.Path, ct));
				line = stored.Anchor.Reattach(lines);
			}
			catch (ToolFailedException)
			{
				// File gone at that revision: outdated.
			}
			reattached.Add(new DraftComment(stored, line));
		}
		Drafts = reattached;
		CommentsChanged?.Invoke();
	}

	async Task LoadPostedCommentsAsync(int number, CancellationToken ct)
	{
		try
		{
			var raw = await GitHub.GetReviewCommentsAsync(number, ct);
			var views = new List<PostedCommentView>();
			foreach (var comment in raw)
			{
				bool oldSide = comment.Side == "LEFT";
				int? line = comment.Line ?? await ReanchorPostedAsync(comment, oldSide, ct);
				views.Add(new PostedCommentView(
					comment.Path, line, oldSide, comment.Body, comment.User?.Login ?? "?"));
			}
			PostedComments = views;
		}
		catch (ToolFailedException)
		{
			PostedComments = [];
		}
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
