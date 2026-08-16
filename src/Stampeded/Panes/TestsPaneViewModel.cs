using System.Collections.ObjectModel;
using System.Text;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;
using Stampeded.Core.Roslyn;
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

	[ObservableProperty]
	string spinner = "";
}

public sealed record TestRow(TestResult Result, string? Marker = null)
{
	public string Display => $"{Marker ?? (Result.Outcome == TestOutcome.Failed ? "[X]" : "[ok]")} {Result.TestName}";
}

/// <summary>
/// Runs `dotnet ...` test commands in the head worktree with live output; failures are
/// listed and navigate to their first stack frame inside the worktree.
/// </summary>
public class TestsPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;
	static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

	readonly StringBuilder outputBuffer = new();
	readonly DispatcherTimer flushTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
	readonly DispatcherTimer spinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
	int spinnerFrame;
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
		spinnerTimer.Tick += (_, _) => {
			spinnerFrame = (spinnerFrame + 1) % SpinnerFrames.Length;
			State.Spinner = SpinnerFrames[spinnerFrame];
			// The dock tab renders Title, so the spinner shows there too.
			Title = $"Tests {State.Spinner}";
		};
		State.PropertyChanged += (_, e) => {
			if (e.PropertyName == nameof(TestsState.Running))
			{
				if (State.Running)
					spinnerTimer.Start();
				else
					spinnerTimer.Stop();
				State.Spinner = State.Running ? SpinnerFrames[0] : "";
				Title = State.Running ? $"Tests {SpinnerFrames[0]}" : "Tests";
			}
		};
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

	public void ApplyImpactedFilter()
	{
		ApplyImpactedFilterAsync().HandleExceptions();

		async Task ApplyImpactedFilterAsync()
		{
			// Three different failures used to share one sentence, which left "it does not
			// work" and "this change has no tests" looking the same. A changed test case is
			// answered by name and needs neither semantics nor a change map, so the checks
			// that follow are about the tracing half alone.
			int fixtures = workspace.AffectedFixtureNames.Count;
			var sem = workspace.Semantics;
			if (fixtures == 0 && sem is not { State: SemanticState.Ready or SemanticState.SyntaxOnly })
			{
				State.Status = $"Semantics are {sem?.State.ToString().ToLowerInvariant() ?? "not loaded"}; "
					+ "the filter needs them to find what references the change.";
				return;
			}
			if (fixtures == 0 && workspace.ChangeMap.Count == 0)
			{
				State.Status = workspace.ChangeMapComputed
					? "No changed members to trace: the change touches no C# member the map knows."
					: "The change map is still being computed; try again in a moment.";
				return;
			}
			State.Status = (fixtures > 0 ? $"{fixtures} changed test case(s); " : "")
				+ $"finding tests referencing {workspace.ChangeMap.Count} changed member(s)"
				+ (sem?.State == SemanticState.SyntaxOnly ? " (syntax-only semantics - references will be incomplete)" : "")
				+ "...";
			var classes = await workspace.SuggestImpactedTestClassesAsync();
			if (classes.Count == 0)
			{
				State.Status = "No test file references the changed members or the types holding them - "
					+ "the change may be untested, or reached only indirectly.";
				return;
			}
			string baseArgs = State.Args;
			int cut = baseArgs.IndexOf(" -- --filter", StringComparison.Ordinal);
			if (cut >= 0)
				baseArgs = baseArgs[..cut];
			string filter = string.Join('|', classes.Select(c => $"FullyQualifiedName~{c}"));
			State.Args = $"{baseArgs} -- --filter \"{filter}\"";
			State.Status = $"Filter set to {classes.Count} impacted test(s): {string.Join(", ", classes)}.";
		}
	}

	public void RunWithCoverage()
	{
		withCoverage = true;
		Run();
	}

	/// <summary>Runs the same test command at base and head and compares VERDICTS: did this
	/// change introduce a failure, or was it already broken at base? Sequential runs (two
	/// concurrent builds would fight over CPU and the NuGet cache).</summary>
	public void RunAB() => RunABAsync().HandleExceptions();

	async Task RunABAsync()
	{
		if (State.Running)
		{
			runCts?.Cancel();
			return;
		}
		// The base side is checked out on demand: reading a diff needs no such thing, running
		// its tests does.
		if (workspace.WorktreePath is not { } head || !Directory.Exists(head)
			|| await workspace.EnsureBaseWorktreeAsync() is not { } baseWt || !Directory.Exists(baseWt))
		{
			State.Status = "A/B needs both worktrees - open a pull request and let semantics load first.";
			return;
		}
		var cts = runCts = new CancellationTokenSource();
		string logDir = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stampeded", "logs");
		Directory.CreateDirectory(logDir);
		runLogFile = Path.Combine(logDir, $"test-ab-{DateTime.Now:yyyyMMdd-HHmmss}.log");
		State.Running = true;
		State.Output = "";
		outputBuffer.Clear();
		Failures.Clear();
		flushTimer.Start();
		RunABCoreAsync(baseWt, head, cts.Token).HandleExceptions();
	}

	async Task RunABCoreAsync(string baseWorktree, string headWorktree, CancellationToken ct)
	{
		using var busy = workspace.Busy.Begin("A/B test run (base, then head)");
		try
		{
			while (workspace.Semantics is { State: Core.Roslyn.SemanticState.Restoring or Core.Roslyn.SemanticState.Loading }
				|| workspace.BaseSemantics is { State: Core.Roslyn.SemanticState.Restoring or Core.Roslyn.SemanticState.Loading })
			{
				State.Status = "Waiting for the semantic workspace load to finish before building tests...";
				await Task.Delay(1000, ct);
			}
			var baseOut = new StringBuilder();
			var headOut = new StringBuilder();

			State.Status = $"A/B 1/2: base run ({workspace.BaseSha?[..9]})...";
			AppendOutput($"==== base @ {workspace.BaseSha} ====");
			var (_, baseResults) = await new TestService(baseWorktree)
				.RunAsync(State.Args, line => { lock (baseOut) baseOut.AppendLine(line); AppendOutput(line); }, ct);

			State.Status = $"A/B 2/2: head run ({workspace.HeadSha?[..9]})...";
			AppendOutput($"==== head @ {workspace.HeadSha} ====");
			var (_, headResults) = await new TestService(headWorktree)
				.RunAsync(State.Args, line => { lock (headOut) headOut.AppendLine(line); AppendOutput(line); }, ct);

			var comparison = TestRunComparison.Compare(baseResults, headResults);
			foreach (var result in comparison.NewlyFailing)
				Failures.Add(new TestRow(result, "[NEW-FAIL]"));
			foreach (var result in comparison.StillFailing)
				Failures.Add(new TestRow(result, "[still]"));
			foreach (var result in comparison.Fixed)
				Failures.Add(new TestRow(result, "[fixed]"));
			State.Status =
				$"A/B: base {comparison.BasePassed} pass / {comparison.BaseFailed} fail; " +
				$"head {comparison.HeadPassed} pass / {comparison.HeadFailed} fail - " +
				$"{comparison.NewlyFailing.Count} newly failing, {comparison.Fixed.Count} fixed, " +
				$"{comparison.StillFailing.Count} already broken at base.  Full log: {runLogFile}";
			workspace.SetTestSummary(
				$"A/B: head {comparison.HeadPassed} pass / {comparison.HeadFailed} fail; " +
				$"{comparison.NewlyFailing.Count} newly failing, {comparison.Fixed.Count} fixed, " +
				$"{comparison.StillFailing.Count} already broken at base");
			workspace.OpenSideBySideText("abtests", "Test output: base | head", baseOut.ToString(), headOut.ToString());
		}
		catch (OperationCanceledException)
		{
			State.Status = "A/B run cancelled.";
		}
		finally
		{
			State.Running = false;
			flushTimer.Stop();
			FlushOutput();
		}
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
			if (results.Count > 0)
				workspace.SetTestSummary($"{passed} passed, {failed.Count} failed, {skipped} skipped{(coverageFile is not null ? " (with coverage)" : "")}");
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
