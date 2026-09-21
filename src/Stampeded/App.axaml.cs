using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

using Stampeded.Core.AzureDevOps;
using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;

using Stampeded.Core.PullRequests;

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
		Program.Host = await PullRequestHosts.ForAsync(path);
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

	static async Task<string?> AskWhereToCloneAsync(Window window, string name)
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
			Title = $"Clone {name} into which folder?",
			AllowMultiple = false,
		};
		if (Directory.Exists(projects))
			options.SuggestedStartLocation = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(projects));
		var picks = await window.StorageProvider.OpenFolderPickerAsync(options);
		return picks.Count == 1 ? picks[0].Path.LocalPath : null;
	}

	/// <summary>Opens a repository or pull-request URL of either host: an already-cloned
	/// repository (any remote matched against the current and recent repos) is reused;
	/// otherwise the folder to clone into is asked for, and a blobless partial clone is made
	/// there.</summary>
	public static async Task OpenFromUrlAsync(string input)
	{
		CliLog.Write("action", $"open from URL {input}");
		int? prNumber;
		// Azure DevOps first: its URLs have no scheme-less form GitHub's grammar refuses, and
		// GitHub's - which also accepts a bare "owner/repo" - would read "dev.azure.com/org/..."
		// as a repository called org owned by dev.azure.com.
		string name, folder;
		Func<string, bool> remoteMatches;
		// The command that makes the clone, given the target directory - which is only known
		// once the reader has said where it goes, and which each of the two spells in its own
		// place on the line.
		Func<string, (string Tool, string[] Args)> clone;
		if (AzureDevOpsUrl.TryParse(input, out string org, out string project, out string adoRepo, out prNumber))
		{
			name = $"{org}/{project}/{adoRepo}";
			folder = adoRepo;
			remoteMatches = remotes => AzureDevOpsUrl.AnyRemoteMatches(remotes, org, project, adoRepo);
			// az has no clone verb, and git's credential helper answers for the login.
			clone = target => ("git", ["clone", "--filter=blob:none",
				$"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}"
					+ $"/_git/{Uri.EscapeDataString(adoRepo)}", target]);
		}
		else if (GitHubUrl.TryParse(input, out string owner, out string repo, out prNumber))
		{
			name = $"{owner}/{repo}";
			folder = repo;
			remoteMatches = remotes => GitHubUrl.AnyRemoteMatches(remotes, owner, repo);
			clone = target => ("gh", ["repo", "clone", $"{owner}/{repo}", target, "--", "--filter=blob:none"]);
		}
		else
		{
			CliLog.Write("action", $"not a GitHub or Azure DevOps repository or PR URL: {input}");
			Workspace?.PostStatus($"Not a GitHub or Azure DevOps repository or PR URL: {input}");
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
				if (remoteMatches(remotes))
				{
					CliLog.Write("action", $"{name} is checked out at {candidate}");
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
		string? parent = await AskWhereToCloneAsync(window, name);
		if (parent is null)
		{
			Workspace?.PostStatus($"Opening {name} cancelled: no folder chosen to clone into.");
			return;
		}
		string target = Path.Combine(parent, folder);
		if (Directory.Exists(target) && !IsRepository(target))
			target = Path.Combine(parent, $"{name.Replace('/', '-')}");
		if (!Directory.Exists(target))
		{
			using var busy = Workspace?.Busy.Begin($"Cloning {name}");
			Workspace?.PostStatus($"Cloning {name} into {target}...");
			try
			{
				// Blobless partial clone: fast even for large repos; worktree checkouts
				// fetch missing blobs on demand.
				var (tool, args) = clone(target);
				await ExternalTool.RunAsync(tool, args, parent);
			}
			catch (ToolFailedException ex)
			{
				CliLog.Write("action", $"clone of {name} failed: {ex.Message}");
				Workspace?.PostStatus($"Clone failed: {ExternalTool.Explain(ex)}");
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
		// Before any window exists, so nothing is built in one theme and repainted in another.
		Themes.ThemeManager.Current.Load();
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
