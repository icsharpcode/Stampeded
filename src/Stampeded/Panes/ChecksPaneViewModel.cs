using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;

namespace Stampeded.Panes;

public sealed partial class ChecksState : ObservableObject
{
	[ObservableProperty]
	string status = "Open a pull request to load its checks.";
}

public sealed record CheckRow(CheckRun Check)
{
	public string Icon => Check.Bucket switch {
		"pass" => "[ok]",
		"fail" => "[X] ",
		"pending" => "[..]",
		"skipping" => "[--]",
		_ => "[??]",
	};

	public string Display => $"{Icon} {Check.Name}{(string.IsNullOrEmpty(Check.Workflow) ? "" : $"  ({Check.Workflow})")}";
}

/// <summary>
/// CI check runs for the open PR's head; double-click a failed check to open its
/// failed-step log as a document.
/// </summary>
public partial class ChecksPaneViewModel : Tool
{
	[GeneratedRegex(@"/actions/runs/(\d+)")]
	private static partial Regex RunIdFromLink();

	readonly ReviewWorkspace workspace;

	public ObservableCollection<CheckRow> Items { get; } = [];
	public ChecksState State { get; } = new();

	public ChecksPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ReviewChanged += () => LoadAsync().HandleExceptions();
	}

	public async Task LoadAsync()
	{
		Items.Clear();
		if (workspace.CurrentPr is not { } pr)
			return;
		State.Status = $"Loading checks for #{pr.Number}...";
		try
		{
			var checks = await workspace.GitHub.GetChecksAsync(pr.Number);
			workspace.SetChecks(checks);
			foreach (var check in checks.OrderBy(c => c.Bucket == "fail" ? 0 : c.Bucket == "pending" ? 1 : 2))
				Items.Add(new CheckRow(check));
			int failed = checks.Count(c => c.Bucket == "fail");
			State.Status = failed > 0
				? $"{failed} of {checks.Count} check(s) failed - double-click one for its log."
				: $"{checks.Count} check(s), none failing.";
		}
		catch (ToolFailedException ex)
		{
			State.Status = ex.Message;
		}
	}

	public void Open(CheckRow row)
	{
		if (row.Check.Link is not { } link || RunIdFromLink().Match(link) is not { Success: true } match)
			return;
		long runId = long.Parse(match.Groups[1].Value);
		State.Status = $"Fetching failed log of run {runId}...";
		OpenLogAsync(runId, row.Check.Name).HandleExceptions();
	}

	async Task OpenLogAsync(long runId, string name)
	{
		try
		{
			string log = await workspace.GitHub.GetFailedLogAsync(runId);
			if (string.IsNullOrWhiteSpace(log))
				log = "(no failed steps in this run)";
			workspace.OpenTextDocument($"cilog:{runId}", $"{name} (failed log)", log);
			State.Status = $"Opened failed log of run {runId}.";
		}
		catch (ToolFailedException ex)
		{
			State.Status = ex.Message;
		}
	}
}
