using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Diff;
using Stampeded.Core.Git;
using Stampeded.Core.GitHub;
using Stampeded.Core.Review;
using Stampeded.Core.Roslyn;
using Stampeded.Documents;
using Stampeded.Navigation;

namespace Stampeded;

sealed record NavEntry(string DockableId, int DocLine) : IEquatable<NavEntry?>;

public sealed record ReferenceItem(string RelPath, int Line, string Preview, bool InChangedLine);

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

	// Set by MainViewModel once the layout exists.
	public Docking.StampededDockFactory? Factory { get; set; }
	public DocumentDock? Documents { get; set; }

	public PrDetail? CurrentPr { get; private set; }
	public string? BaseSha { get; private set; }
	public string? HeadSha { get; private set; }
	public IReadOnlyList<FileDiff> Files { get; private set; } = [];
	public RoslynWorkspaceService? Semantics { get; private set; }
	public string? WorktreePath { get; private set; }

	public event Action? ReviewChanged;
	public event Action<string, bool>? ViewedChanged;
	public event Action? SemanticsChanged;
	public event Action<string, IReadOnlyList<ReferenceItem>>? ReferencesAvailable;

	CancellationTokenSource? sessionCts;
	Dictionary<string, HashSet<int>> addedLinesByFile = [];
	readonly NavigationHistory<NavEntry> history = new();

	public async Task OpenPrAsync(int number)
	{
		sessionCts?.Cancel();
		var cts = sessionCts = new CancellationTokenSource();
		var ct = cts.Token;

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
		LoadSemanticsAsync(headSha, ct).HandleExceptions();
	}

	async Task LoadSemanticsAsync(string headSha, CancellationToken ct)
	{
		Semantics?.Dispose();
		var semantics = Semantics = new RoslynWorkspaceService();
		semantics.StateChanged += () => SemanticsChanged?.Invoke();
		SemanticsChanged?.Invoke();
		WorktreePath = await Worktrees.GetOrCreateAsync(headSha, ct);
		await Task.Run(() => semantics.LoadAsync(WorktreePath, ct), ct);
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

	bool SemanticsReady => Semantics is { State: SemanticState.Ready or SemanticState.SyntaxOnly };

	/// <summary>Go to definition of the symbol at (newLine, column) of a reviewed file.</summary>
	public async Task NavigateToDefinitionAsync(string relPath, int newLine, int column, NavEntryOrigin origin)
	{
		if (Semantics is not { } sem || !SemanticsReady)
			return;
		int? position = await sem.GetPositionAsync(relPath, newLine, column, CancellationToken.None);
		if (position is null)
			return;
		var symbol = await sem.GetSymbolAtAsync(relPath, position.Value, CancellationToken.None);
		if (symbol is null)
			return;
		var location = sem.GetDefinitionLocation(symbol);
		if (location is null)
			return;
		string? targetRel = sem.ToRelativePath(location.FilePath);
		if (targetRel is null)
			return;
		RecordOrigin(origin);
		await NavigateToFileLineAsync(targetRel, location.Line, record: true);
	}

	public async Task ShowReferencesAtAsync(string relPath, int newLine, int column)
	{
		if (Semantics is not { } sem || !SemanticsReady)
			return;
		int? position = await sem.GetPositionAsync(relPath, newLine, column, CancellationToken.None);
		if (position is null)
			return;
		var symbol = await sem.GetSymbolAtAsync(relPath, position.Value, CancellationToken.None);
		if (symbol is null)
			return;
		var hits = await sem.FindReferencesAsync(symbol, CancellationToken.None);
		var items = hits
			.Select(h => (Rel: sem.ToRelativePath(h.FilePath), Hit: h))
			.Where(x => x.Rel is not null)
			.Select(x => new ReferenceItem(
				x.Rel!, x.Hit.Line, x.Hit.LineText,
				addedLinesByFile.TryGetValue(x.Rel!, out var lines) && lines.Contains(x.Hit.Line)))
			.ToList();
		ReferencesAvailable?.Invoke(symbol.Name, items);
	}

	/// <summary>Opens (or activates) a file at a NEW-file line: as its review diff when the
	/// file is part of the PR, else as a read-only source view of the head worktree.</summary>
	public async Task NavigateToFileLineAsync(string relPath, int fileLine, bool record)
	{
		DiffDocumentViewModel? vm;
		int docLine;
		var fileDiff = Files.FirstOrDefault(f => f.Path == relPath);
		if (fileDiff is not null)
		{
			vm = await OpenFileAsync(fileDiff);
			docLine = vm?.Model.DocLineFromNewLine(fileLine) ?? fileLine;
		}
		else
		{
			if (WorktreePath is null)
				return;
			string absolute = Path.Combine(WorktreePath, relPath);
			if (!File.Exists(absolute))
				return;
			vm = ShowDocument("src:" + relPath, () => {
				string text = File.ReadAllText(absolute);
				return DiffDocumentViewModel.ForSource(relPath, text);
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
		if (WorktreePath is not null)
		{
			string absolute = Path.Combine(WorktreePath, relPath);
			if (File.Exists(absolute))
			{
				var vm = ShowDocument("src:" + relPath, () => DiffDocumentViewModel.ForSource(relPath, File.ReadAllText(absolute)));
				vm?.RequestCaret(entry.DocLine);
			}
		}
	}

	#endregion
}
