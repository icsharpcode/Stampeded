using System.Collections.ObjectModel;
using System.Text;

using Avalonia.Threading;

using CliWrap;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;
using Stampeded.Core.Roslyn;

namespace Stampeded.Panes;

public sealed partial class RunState : ObservableObject
{
	[ObservableProperty]
	string? selectedProject;

	[ObservableProperty]
	string arguments = "";

	[ObservableProperty]
	string output = "";

	[ObservableProperty]
	string status = "Open a review, pick a startup project, then Run it from the head worktree.";

	[ObservableProperty]
	bool running;
}

/// <summary>
/// Runs a selected executable project from the head worktree (`dotnet run --project X`),
/// so the PR's own build can be exercised interactively during review.
/// </summary>
public class RunPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	readonly StringBuilder outputBuffer = new();
	readonly DispatcherTimer flushTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
	CancellationTokenSource? runCts;

	public ObservableCollection<string> Projects { get; } = [];
	public RunState State { get; } = new();

	public RunPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ReviewChanged += () => Dispatcher.UIThread.Post(() => {
			Projects.Clear();
			DiscoverProjects();
		});
		workspace.SemanticsChanged += () => Dispatcher.UIThread.Post(DiscoverProjects);
		flushTimer.Tick += (_, _) => FlushOutput();
	}

	void DiscoverProjects()
	{
		if (Projects.Count > 0 || workspace.WorktreePath is not { } worktree || !Directory.Exists(worktree))
			return;
		var executables = new List<string>();
		var libraries = new List<string>();
		foreach (var csproj in Directory.EnumerateFiles(worktree, "*.csproj", SearchOption.AllDirectories))
		{
			if (csproj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
				|| csproj.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
				continue;
			string relative = csproj[(worktree.Length + 1)..].Replace('\\', '/');
			string content;
			try
			{
				content = File.ReadAllText(csproj);
			}
			catch (IOException)
			{
				continue;
			}
			if (content.Contains("<OutputType>Exe", StringComparison.OrdinalIgnoreCase)
				|| content.Contains("<OutputType>WinExe", StringComparison.OrdinalIgnoreCase))
				executables.Add(relative);
			else
				libraries.Add(relative);
		}
		foreach (var project in executables.OrderBy(p => p, StringComparer.Ordinal))
			Projects.Add(project);
		foreach (var project in libraries.OrderBy(p => p, StringComparer.Ordinal))
			Projects.Add(project);
		if (State.SelectedProject is null || !Projects.Contains(State.SelectedProject))
			State.SelectedProject = executables.FirstOrDefault();
		if (Projects.Count > 0)
			State.Status = $"{executables.Count} executable project(s) found in the worktree.";
	}

	public void ClearProjectsForNewReview()
	{
		Projects.Clear();
	}

	public void Run()
	{
		if (State.Running)
		{
			runCts?.Cancel();
			return;
		}
		if (workspace.WorktreePath is not { } worktree || State.SelectedProject is not { } project)
		{
			State.Status = "Open a review and select a project first.";
			return;
		}
		var cts = runCts = new CancellationTokenSource();
		State.Running = true;
		State.Output = "";
		outputBuffer.Clear();
		flushTimer.Start();
		RunCoreAsync(worktree, project, cts.Token).HandleExceptions();
	}

	async Task RunCoreAsync(string worktree, string project, CancellationToken ct)
	{
		using var busy = workspace.Busy.Begin($"Running {Path.GetFileNameWithoutExtension(project)}");
		try
		{
			while (workspace.Semantics is { State: SemanticState.Restoring or SemanticState.Loading }
				|| workspace.BaseSemantics is { State: SemanticState.Restoring or SemanticState.Loading })
			{
				State.Status = "Waiting for the semantic workspace load to finish before building...";
				await Task.Delay(1000, ct);
			}
			State.Status = $"Running: dotnet run --project {project}";
			CliLog.Write("dotnet", $"run --project {project} (started)");
			var args = new List<string> { "run", "--project", project };
			if (State.Arguments.Trim().Length > 0)
			{
				args.Add("--");
				args.AddRange(State.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
			}
			var result = await Cli.Wrap("dotnet")
				.WithArguments(args)
				.WithWorkingDirectory(worktree)
				.WithEnvironmentVariables(env => {
					env.Set("OPENSSL_ENABLE_SHA1_SIGNATURES", "1");
					ExternalTool.StripMsBuildLocatorVariables(env);
				})
				.WithValidation(CommandResultValidation.None)
				.WithStandardOutputPipe(PipeTarget.ToDelegate(AppendOutput))
				.WithStandardErrorPipe(PipeTarget.ToDelegate(AppendOutput))
				.ExecuteAsync(ct);
			CliLog.Write("dotnet", $"run --project {project} -> exit {result.ExitCode}");
			State.Status = $"Exited with code {result.ExitCode}.";
		}
		catch (OperationCanceledException)
		{
			State.Status = "Stopped.";
		}
		finally
		{
			State.Running = false;
			flushTimer.Stop();
			FlushOutput();
		}
	}

	void AppendOutput(string line)
	{
		lock (outputBuffer)
			outputBuffer.AppendLine(line);
	}

	void FlushOutput()
	{
		string chunk;
		lock (outputBuffer)
		{
			if (outputBuffer.Length == 0)
				return;
			chunk = outputBuffer.ToString();
			outputBuffer.Clear();
		}
		State.Output += chunk;
	}
}
