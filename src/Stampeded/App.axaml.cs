using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;

namespace Stampeded;

public class App : Application
{
	/// <summary>The one review workspace of this app instance; set by MainViewModel.</summary>
	public static ReviewWorkspace? Workspace { get; set; }

	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
	}

	/// <summary>Switches the app to another repository: the current workspace is shut
	/// down and the whole layout is rebuilt over the new path.</summary>
	public static async Task OpenRepositoryAsync(string path, int? prNumber = null)
	{
		if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
			|| desktop.MainWindow is not MainWindow window)
			return;
		string gitMarker = Path.Combine(path, ".git");
		if (!Directory.Exists(path) || !(Directory.Exists(gitMarker) || File.Exists(gitMarker)))
		{
			Workspace?.PostStatus($"Not a git repository: {path}");
			return;
		}
		CliLog.Write("action", $"open repository {path}");
		Workspace?.Shutdown();
		Program.RepoPath = path;
		window.DataContext = new MainViewModel();
		if (prNumber is { } pr)
			await (Workspace?.OpenPrAsync(pr) ?? Task.CompletedTask);
	}

	static bool IsRepository(string path)
		=> Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

	/// <summary>
	/// Asks which folder the clone should be made in, offering ~/Projects as a starting point
	/// when it exists. Null when the question is declined - the answer decides where a
	/// repository lands on disk, and there is no sensible default to assume on someone's
	/// behalf.
	/// </summary>
	/// <summary>
	/// An answer for the next folder question, instead of asking it. The picker is the
	/// desktop portal's own dialog and nothing inside this process can drive it, so the two
	/// paths that lead out of it - a folder, or a decline - would otherwise never be walked
	/// by a check. Set by the screenshot harness and consumed by the next question.
	/// </summary>
	internal static (string? Folder, bool Answered) NextFolderAnswer;

	static async Task<string?> AskWhereToCloneAsync(Window window, string owner, string repo)
	{
		if (NextFolderAnswer.Answered)
		{
			var answer = NextFolderAnswer.Folder;
			NextFolderAnswer = default;
			CliLog.Write("action", $"clone folder answered as {answer ?? "(declined)"}");
			return answer;
		}
		string projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects");
		var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions {
			Title = $"Clone {owner}/{repo} into which folder?",
			AllowMultiple = false,
		};
		if (Directory.Exists(projects))
			options.SuggestedStartLocation = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(projects));
		var picks = await window.StorageProvider.OpenFolderPickerAsync(options);
		return picks.Count == 1 ? picks[0].Path.LocalPath : null;
	}

	/// <summary>Opens a GitHub repo/PR URL: an already-cloned repository (origin remote
	/// matched against the current and recent repos) is reused; otherwise the folder to clone
	/// into is asked for, and gh makes a blobless partial clone there.</summary>
	public static async Task OpenFromUrlAsync(string input)
	{
		CliLog.Write("action", $"open from URL {input}");
		if (!GitHubUrl.TryParse(input, out string owner, out string repo, out int? prNumber))
		{
			CliLog.Write("action", $"not a GitHub repository or PR URL: {input}");
			Workspace?.PostStatus($"Not a GitHub repository or PR URL: {input}");
			return;
		}
		var candidates = new List<string> { Program.RepoPath };
		candidates.AddRange(RecentRepos.Load());
		foreach (var candidate in candidates.Distinct())
		{
			try
			{
				// Every remote, not just origin: a checkout that tracks both a repository and
				// a fork of it is the right checkout for a URL naming either.
				string remotes = await ExternalTool.RunAsync(
					"git", ["-C", candidate, "config", "--get-regexp", @"^remote\..*\.url"], candidate);
				if (GitHubUrl.AnyRemoteMatches(remotes, owner, repo))
				{
					CliLog.Write("action", $"{owner}/{repo} is checked out at {candidate}");
					await OpenRepositoryAsync(candidate, prNumber);
					return;
				}
			}
			catch (ToolFailedException)
			{
				// No origin remote or the path is gone; not a match.
			}
		}
		// Nothing local matches, so it has to be cloned - and where a clone goes is the
		// user's business, not a guess about how their disk is arranged.
		if ((Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is not { } window)
			return;
		string? parent = await AskWhereToCloneAsync(window, owner, repo);
		if (parent is null)
		{
			Workspace?.PostStatus($"Opening {owner}/{repo} cancelled: no folder chosen to clone into.");
			return;
		}
		string target = Path.Combine(parent, repo);
		if (Directory.Exists(target) && !IsRepository(target))
			target = Path.Combine(parent, $"{owner}-{repo}");
		if (!Directory.Exists(target))
		{
			using var busy = Workspace?.Busy.Begin($"Cloning {owner}/{repo}");
			Workspace?.PostStatus($"Cloning {owner}/{repo} into {target}...");
			try
			{
				// Blobless partial clone: fast even for large repos; worktree checkouts
				// fetch missing blobs on demand.
				await ExternalTool.RunAsync("gh", ["repo", "clone", $"{owner}/{repo}", target, "--", "--filter=blob:none"], parent);
			}
			catch (ToolFailedException ex)
			{
				CliLog.Write("action", $"clone of {owner}/{repo} failed: {ex.Message}");
				Workspace?.PostStatus($"Clone failed: {ex.Message}");
				return;
			}
		}
		await OpenRepositoryAsync(target, prNumber);
	}

	static Task OpenAutoPrAsync(int pr)
	{
		if (Workspace?.StartPage is { } start)
			start.OpenPrNumber(pr);
		else
			Workspace?.OpenPrAsync(pr).HandleExceptions();
		return Task.CompletedTask;
	}

	/// <summary>
	/// Held for the process's lifetime so the registrations stay alive. A language server is
	/// a child process that outlives an unclean exit: it is told to leave when the window
	/// closes, and this is the other way out - a terminal that sends SIGINT, a session
	/// manager that sends SIGTERM.
	/// </summary>
	static readonly List<IDisposable> signalHandlers = [];

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new MainWindow();
			desktop.ShutdownRequested += (_, _) => Workspace?.Shutdown();
			foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
			{
				signalHandlers.Add(PosixSignalRegistration.Create(signal, context => {
					// Not cancelled: the process is still going to end, and holding it open
					// to finish tidying is how a kill becomes a kill -9.
					Workspace?.Shutdown();
					CliLog.Write("app", $"{context.Signal}: stopped the review's servers");
				}));
			}
			if (Program.AutoOpenPr is { } pr)
			{
				// --pr N means "open guided": land the wizard on Triage like Open Guided does.
				OpenAutoPrAsync(pr).HandleExceptions();
			}
		}
		base.OnFrameworkInitializationCompleted();
	}
}
