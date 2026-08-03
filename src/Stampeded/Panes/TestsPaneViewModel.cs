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
	string? runLogFile;
	bool withCoverage;

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
		// Full solutions often contain platform-bound projects (net472 add-ins,
		// net*-windows test hosts) that cannot build off-Windows; prefer a
		// cross-platform solution filter when the repo ships one (e.g. ILSpy.XPlat.slnf).
		if (!OperatingSystem.IsWindows())
		{
			string? slnf = Directory.EnumerateFiles(root, "*.slnf", SearchOption.TopDirectoryOnly)
				.FirstOrDefault(f => Path.GetFileName(f).Contains("xplat", StringComparison.OrdinalIgnoreCase));
			if (slnf is not null)
				return $"test --solution {Path.GetFileName(slnf)} --report-trx";
		}
		string? sln = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly)
			.OrderByDescending(f => new FileInfo(f).Length)
			.FirstOrDefault();
		string name = sln is null ? "" : Path.GetFileName(sln);
		// The --solution/--report-trx form targets the Microsoft.Testing.Platform runner;
		// repos on classic VSTest still accept an explicit path argument instead.
		return $"test --solution {name} --report-trx";
	}

	public void RunWithCoverage()
	{
		withCoverage = true;
		Run();
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
		string logDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stampeded", "logs");
		Directory.CreateDirectory(logDir);
		runLogFile = Path.Combine(logDir, $"test-{DateTime.Now:yyyyMMdd-HHmmss}.log");
		State.Running = true;
		State.Status = $"Running: dotnet {State.Args}  (full log: {runLogFile})";
		State.Output = "";
		outputBuffer.Clear();
		Failures.Clear();
		flushTimer.Start();
		RunCoreAsync(worktree, cts.Token).HandleExceptions();
	}

	async Task RunCoreAsync(string worktree, CancellationToken ct)
	{
		using var busy = workspace.Busy.Begin("Running tests");
		try
		{
			// The semantic load runs dotnet restore and design-time builds in this same
			// worktree; a concurrent test build trips over the half-written obj/ state
			// and dies with an opaque "Build failed" (MTP hides MSBuild's errors).
			while (workspace.Semantics is { State: Core.Roslyn.SemanticState.Restoring or Core.Roslyn.SemanticState.Loading }
				|| workspace.BaseSemantics is { State: Core.Roslyn.SemanticState.Restoring or Core.Roslyn.SemanticState.Loading })
			{
				State.Status = "Waiting for the semantic workspace load to finish before building tests...";
				await Task.Delay(1000, ct);
			}
			State.Status = $"Running: dotnet {State.Args}  (full log: {runLogFile})";
			var service = new TestService(worktree);
			string? coverageFile = withCoverage
				? Path.ChangeExtension(runLogFile!, ".cobertura.xml")
				: null;
			withCoverage = false;
			var (exitCode, results) = await service.RunAsync(State.Args, AppendOutput, ct, coverageFile);
			string coverageNote = "";
			if (coverageFile is not null && File.Exists(coverageFile))
			{
				var coverage = Core.Testing.CoberturaParser.Parse(File.ReadAllText(coverageFile), worktree);
				workspace.SetCoverage(coverage);
				coverageNote = $"  Coverage: {coverage.Count} file(s) overlaid.";
			}
			else if (coverageFile is not null)
			{
				coverageNote = "  Coverage: no report produced (is dotnet-coverage installed? dotnet tool install -g dotnet-coverage).";
			}
			var failed = results.Where(r => r.Outcome == TestOutcome.Failed).ToList();
			foreach (var failure in failed)
				Failures.Add(new TestRow(failure));
			int passed = results.Count(r => r.Outcome == TestOutcome.Passed);
			int skipped = results.Count(r => r.Outcome == TestOutcome.Skipped);
			string logHint = (runLogFile is null ? "" : $"  Full log: {runLogFile}") + coverageNote;
			State.Status = (results.Count == 0
				? $"Run finished (exit {exitCode}) - no .trx results found."
				: $"{passed} passed, {failed.Count} failed, {skipped} skipped. Double-click a failure to open its frame.") + logHint;
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
		string chunk;
		lock (outputBuffer)
		{
			if (outputBuffer.Length == 0)
				return;
			chunk = outputBuffer.ToString();
			outputBuffer.Clear();
		}
		State.Output += chunk;
		if (runLogFile is not null)
		{
			try
			{
				File.AppendAllText(runLogFile, chunk);
			}
			catch (IOException)
			{
				// Logging must never break the run; the pane still holds the output.
			}
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
