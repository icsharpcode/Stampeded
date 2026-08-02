using System.Collections.ObjectModel;
using System.Text;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Testing;

namespace Stampeded.Panes;

public sealed partial class TestsState : ObservableObject
{
	[ObservableProperty]
	string args = "";

	[ObservableProperty]
	string output = "";

	[ObservableProperty]
	string status = "Open a pull request, then run tests against its head worktree.";

	[ObservableProperty]
	bool running;
}

public sealed record TestRow(TestResult Result)
{
	public string Display => $"{(Result.Outcome == TestOutcome.Failed ? "[X]" : "[ok]")} {Result.TestName}";
}

/// <summary>
/// Runs `dotnet ...` test commands in the head worktree with live output; failures are
/// listed and navigate to their first stack frame inside the worktree.
/// </summary>
public class TestsPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	readonly StringBuilder outputBuffer = new();
	readonly DispatcherTimer flushTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
	CancellationTokenSource? runCts;

	public ObservableCollection<TestRow> Failures { get; } = [];
	public TestsState State { get; } = new();

	public TestsPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ReviewChanged += OnReviewChanged;
		flushTimer.Tick += (_, _) => FlushOutput();
	}

	void OnReviewChanged()
	{
		Failures.Clear();
		State.Output = "";
		State.Status = "Ready to run tests against the head worktree.";
		if (State.Args.Length == 0 && workspace.WorktreePath is { } wt)
		{
			// Worktree may still be in creation; fall back to the source repo for discovery.
			State.Args = DefaultArgs(Directory.Exists(wt) ? wt : workspace.RepoPath);
		}
		else if (State.Args.Length == 0)
		{
			State.Args = DefaultArgs(workspace.RepoPath);
		}
	}

	static string DefaultArgs(string root)
	{
		string? sln = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
			.OrderByDescending(f => new FileInfo(f).Length)
			.FirstOrDefault();
		string name = sln is null ? "" : Path.GetFileName(sln);
		// The --solution/--report-trx form targets the Microsoft.Testing.Platform runner;
		// repos on classic VSTest still accept an explicit path argument instead.
		return $"test --solution {name} --report-trx";
	}

	public void Run()
	{
		if (State.Running)
		{
			runCts?.Cancel();
			return;
		}
		if (workspace.WorktreePath is not { } worktree || !Directory.Exists(worktree))
		{
			State.Status = "No head worktree yet - open a pull request first.";
			return;
		}
		var cts = runCts = new CancellationTokenSource();
		State.Running = true;
		State.Status = "Running: dotnet " + State.Args;
		State.Output = "";
		outputBuffer.Clear();
		Failures.Clear();
		flushTimer.Start();
		RunCoreAsync(worktree, cts.Token).HandleExceptions();
	}

	async Task RunCoreAsync(string worktree, CancellationToken ct)
	{
		try
		{
			var service = new TestService(worktree);
			var (exitCode, results) = await service.RunAsync(State.Args, AppendOutput, ct);
			var failed = results.Where(r => r.Outcome == TestOutcome.Failed).ToList();
			foreach (var failure in failed)
				Failures.Add(new TestRow(failure));
			int passed = results.Count(r => r.Outcome == TestOutcome.Passed);
			int skipped = results.Count(r => r.Outcome == TestOutcome.Skipped);
			State.Status = results.Count == 0
				? $"Run finished (exit {exitCode}) - no .trx results found."
				: $"{passed} passed, {failed.Count} failed, {skipped} skipped. Double-click a failure to open its frame.";
		}
		catch (OperationCanceledException)
		{
			State.Status = "Test run cancelled.";
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
		lock (outputBuffer)
		{
			if (outputBuffer.Length == 0)
				return;
			State.Output += outputBuffer.ToString();
			outputBuffer.Clear();
		}
	}

	public void Open(TestRow row)
	{
		if (row.Result.TryGetSourceLocation() is not { } location || workspace.WorktreePath is not { } worktree)
			return;
		string full = Path.GetFullPath(location.FilePath);
		string root = Path.GetFullPath(worktree);
		if (!full.StartsWith(root, StringComparison.Ordinal))
			return;
		string rel = full[(root.Length + 1)..].Replace('\\', '/');
		workspace.NavigateToFileLineAsync(rel, location.Line, oldSide: false, record: true).HandleExceptions();
	}
}
