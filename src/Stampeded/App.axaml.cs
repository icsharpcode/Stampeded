using Avalonia;
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

	/// <summary>Opens a GitHub repo/PR URL: an already-cloned repository (origin remote
	/// matched against the current and recent repos) is reused, else gh clones it as a
	/// blobless partial clone under ~/Projects.</summary>
	public static async Task OpenFromUrlAsync(string input)
	{
		if (!GitHubUrl.TryParse(input, out string owner, out string repo, out int? prNumber))
		{
			Workspace?.PostStatus($"Not a GitHub repository or PR URL: {input}");
			return;
		}
		var candidates = new List<string> { Program.RepoPath };
		candidates.AddRange(RecentRepos.Load());
		foreach (var candidate in candidates.Distinct())
		{
			try
			{
				string remote = (await ExternalTool.RunAsync("git", ["-C", candidate, "remote", "get-url", "origin"], candidate)).Trim();
				if (GitHubUrl.RemoteMatches(remote, owner, repo))
				{
					await OpenRepositoryAsync(candidate, prNumber);
					return;
				}
			}
			catch (ToolFailedException)
			{
				// No origin remote or the path is gone; not a match.
			}
		}
		string projects = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Projects");
		string target = Path.Combine(projects, repo);
		if (Directory.Exists(target))
			target = Path.Combine(projects, $"{owner}-{repo}");
		if (!Directory.Exists(target))
		{
			using var busy = Workspace?.Busy.Begin($"Cloning {owner}/{repo}");
			Workspace?.PostStatus($"Cloning {owner}/{repo} into {target}...");
			try
			{
				// Blobless partial clone: fast even for large repos; worktree checkouts
				// fetch missing blobs on demand.
				await ExternalTool.RunAsync("gh", ["repo", "clone", $"{owner}/{repo}", target, "--", "--filter=blob:none"], projects);
			}
			catch (ToolFailedException ex)
			{
				Workspace?.PostStatus($"Clone failed: {ex.Message}");
				return;
			}
		}
		await OpenRepositoryAsync(target, prNumber);
	}

	static async Task OpenAutoPrAsync(int pr)
	{
		if (Workspace is not { } workspace)
			return;
		await workspace.OpenPrAsync(pr, guided: true);
		var wizard = workspace.Documents?.VisibleDockables?.OfType<Documents.WizardViewModel>().FirstOrDefault();
		if (wizard is not null)
			Avalonia.Threading.Dispatcher.UIThread.Post(() => wizard.SelectStepCommand(wizard.TriageStep));
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new MainWindow();
			if (Program.AutoOpenPr is { } pr)
			{
				// --pr N means "open guided": land the wizard on Triage like Open Guided does.
				OpenAutoPrAsync(pr).HandleExceptions();
			}
		}
		base.OnFrameworkInitializationCompleted();
	}
}
