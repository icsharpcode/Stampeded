using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Diff;
using Stampeded.Core.Git;
using Stampeded.Core.GitHub;
using Stampeded.Core.Review;
using Stampeded.Documents;

namespace Stampeded;

/// <summary>
/// The open review session: which PR, its base/head SHAs, its file diffs, and the
/// review-progress store. Orchestrates git/gh access and document opening.
/// </summary>
public sealed class ReviewWorkspace(string repoPath)
{
	public string RepoPath { get; } = repoPath;
	public GitService Git { get; } = new(repoPath);
	public GitHubService GitHub { get; } = new(repoPath);
	public ReviewStateStore Store { get; } = new();

	// Set by MainViewModel once the layout exists.
	public Docking.StampededDockFactory? Factory { get; set; }
	public DocumentDock? Documents { get; set; }

	public PrDetail? CurrentPr { get; private set; }
	public string? BaseSha { get; private set; }
	public string? HeadSha { get; private set; }
	public IReadOnlyList<FileDiff> Files { get; private set; } = [];

	public event Action? ReviewChanged;
	public event Action<string, bool>? ViewedChanged;

	CancellationTokenSource? sessionCts;

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
		Store.Open(Path.GetFileName(RepoPath), number, headSha);
		CloseOpenDiffs();
		ReviewChanged?.Invoke();
	}

	void CloseOpenDiffs()
	{
		if (Documents?.VisibleDockables is null || Factory is null)
			return;
		foreach (var diff in Documents.VisibleDockables.OfType<DiffDocumentViewModel>().ToList())
			Factory.CloseDockable(diff);
	}

	public async Task OpenFileAsync(FileDiff file)
	{
		if (BaseSha is null || HeadSha is null || Documents is null || Factory is null)
			return;

		var existing = Documents.VisibleDockables?
			.OfType<DiffDocumentViewModel>()
			.FirstOrDefault(d => d.File.Path == file.Path);
		if (existing is not null)
		{
			Factory.SetActiveDockable(existing);
			Factory.SetFocusedDockable(Documents, existing);
			return;
		}

		string oldText = file.Kind == FileChangeKind.Added || file.IsBinary
			? ""
			: await Git.ShowFileAsync(BaseSha, file.OldPath);
		string newText = file.Kind == FileChangeKind.Deleted || file.IsBinary
			? ""
			: await Git.ShowFileAsync(HeadSha, file.NewPath);
		var model = DiffDocumentBuilder.Build(oldText, newText);

		var vm = new DiffDocumentViewModel(file, model) {
			Id = "diff:" + file.Path,
			Title = Path.GetFileName(file.Path),
		};
		Factory.AddDockable(Documents, vm);
		Factory.SetActiveDockable(vm);
		Factory.SetFocusedDockable(Documents, vm);
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
}
