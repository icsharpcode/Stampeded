using Stampeded.Core.AzureDevOps;
using Stampeded.Core.Git;
using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;

namespace Stampeded.Core.PullRequests;

/// <summary>Decides which host a checkout's pull requests live on, once per workspace.</summary>
public static class PullRequestHosts
{
	/// <summary>
	/// The host for a checkout, from its remote's URL (see <see cref="GitService.GetRemoteAsync"/>
	/// for which remote that is). Anything that URL does not name as Azure DevOps is GitHub on
	/// purpose: gh also serves GitHub Enterprise hosts, which nothing here can enumerate, and a
	/// clone with no usable remote at all behaves as it always did.
	/// <c>STAMPEDED_PR_HOST=github|azdo</c> overrides the decision.
	/// </summary>
	public static async Task<IPullRequestHost> ForAsync(string repoPath, CancellationToken ct = default)
	{
		var (remote, url) = await RemoteUrlAsync(repoPath, ct);
		string? forced = Environment.GetEnvironmentVariable("STAMPEDED_PR_HOST");
		bool azdo = AzureDevOpsUrl.TryParse(url, out string org, out string project, out string repo, out _);
		if (forced is { Length: > 0 })
		{
			CliLog.Write("host", $"STAMPEDED_PR_HOST={forced}");
			azdo = forced.Equals("azdo", StringComparison.OrdinalIgnoreCase);
		}
		if (!azdo)
		{
			CliLog.Write("host", $"{remote ?? "the repository"} is GitHub");
			return new GitHubService(repoPath);
		}
		CliLog.Write("host", $"{remote ?? "the repository"} is Azure DevOps ({org}/{project}/{repo})");
		return new AzureDevOpsService(repoPath, org, project, repo);
	}

	static async Task<(string? Remote, string Url)> RemoteUrlAsync(string repoPath, CancellationToken ct)
	{
		string remote;
		try
		{
			remote = await new GitService(repoPath).GetRemoteAsync(ct);
		}
		catch (ToolFailedException)
		{
			// Not a repository, or no remote to tell the host by - a clone that was never pushed
			// anywhere, or a review of local work. The reason is already in the log, and every
			// operation that needs the remote reports it again where it is attempted.
			return (null, "");
		}
		// Exit 1 is git saying the key is not set, which is an answer, not a failure.
		return (remote, (await ExternalTool.RunAsync("git", ["config", "--get", $"remote.{remote}.url"], repoPath, ct,
			okExitCodes: [1])).Trim());
	}
}
