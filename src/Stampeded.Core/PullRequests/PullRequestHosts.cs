using Stampeded.Core.AzureDevOps;
using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;

namespace Stampeded.Core.PullRequests;

/// <summary>Decides which host a checkout's pull requests live on, once per workspace.</summary>
public static class PullRequestHosts
{
	/// <summary>
	/// The host for a checkout, from origin's URL. Anything origin's URL does not name as Azure
	/// DevOps is GitHub on purpose: gh also serves GitHub Enterprise hosts, which nothing here
	/// can enumerate, and a clone with no origin at all behaves as it always did.
	/// <c>STAMPEDED_PR_HOST=github|azdo</c> overrides the decision.
	/// </summary>
	public static async Task<IPullRequestHost> ForAsync(string repoPath, CancellationToken ct = default)
	{
		string origin = await OriginUrlAsync(repoPath, ct);
		string? forced = Environment.GetEnvironmentVariable("STAMPEDED_PR_HOST");
		bool azdo = AzureDevOpsUrl.TryParse(origin, out string org, out string project, out string repo, out _);
		if (forced is { Length: > 0 })
		{
			CliLog.Write("host", $"STAMPEDED_PR_HOST={forced}");
			azdo = forced.Equals("azdo", StringComparison.OrdinalIgnoreCase);
		}
		if (!azdo)
		{
			CliLog.Write("host", "origin is GitHub");
			return new GitHubService(repoPath);
		}
		CliLog.Write("host", $"origin is Azure DevOps ({org}/{project}/{repo})");
		return new AzureDevOpsService(repoPath, org, project, repo);
	}

	static async Task<string> OriginUrlAsync(string repoPath, CancellationToken ct)
	{
		// Exit 1 is git saying the key is not set: a clone that was never pushed anywhere, or
		// a review of local work. That is an answer, not a failure.
		return (await ExternalTool.RunAsync("git", ["config", "--get", "remote.origin.url"], repoPath, ct,
			okExitCodes: [1])).Trim();
	}
}
