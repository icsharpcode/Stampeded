using NUnit.Framework;

using Stampeded.Core.Infra;
using Stampeded.Core.PullRequests;

namespace Stampeded.Core.Tests;

public class PullRequestHostsTests
{
	string? oldForced;
	string? oldBitbucketBase;
	readonly List<string> dirs = [];

	[SetUp]
	public void ClearForcedHost()
	{
		oldForced = Environment.GetEnvironmentVariable("STAMPEDED_PR_HOST");
		oldBitbucketBase = Environment.GetEnvironmentVariable("STAMPEDED_BITBUCKET_BASE_URL");
		Environment.SetEnvironmentVariable("STAMPEDED_PR_HOST", null);
		Environment.SetEnvironmentVariable("STAMPEDED_BITBUCKET_BASE_URL", null);
	}

	[TearDown]
	public void RestoreForcedHost()
	{
		Environment.SetEnvironmentVariable("STAMPEDED_PR_HOST", oldForced);
		Environment.SetEnvironmentVariable("STAMPEDED_BITBUCKET_BASE_URL", oldBitbucketBase);
		foreach (string dir in dirs)
			TempDirectory.Delete(dir);
		dirs.Clear();
	}

	[Test]
	public async Task SelectsGitHubFromGithubOrigin()
	{
		string repo = await NewRepoAsync("https://github.com/icsharpcode/ILSpy.git");

		var host = await PullRequestHosts.TryForAsync(repo);

		Assert.That(host?.Name, Is.EqualTo("GitHub"));
	}

	[Test]
	public async Task SelectsGitHubFromGithubSshOrigin()
	{
		string repo = await NewRepoAsync("git@github.com:icsharpcode/ILSpy.git");

		var host = await PullRequestHosts.TryForAsync(repo);

		Assert.That(host?.Name, Is.EqualTo("GitHub"));
	}

	[Test]
	public async Task ReturnsNullWhenOriginDoesNotNameASupportedHost()
	{
		string repo = await NewRepoAsync("https://gitlab.example.com/group/repo.git");

		var host = await PullRequestHosts.TryForAsync(repo);

		Assert.That(host, Is.Null);
	}

	[Test]
	public async Task ReturnsNullWhenOriginIsMissing()
	{
		string repo = await NewRepoAsync(null);

		var host = await PullRequestHosts.TryForAsync(repo);

		Assert.That(host, Is.Null);
	}

	[Test]
	public async Task BitbucketBaseUrlOverrideSuppliesTheWebAndApiBase()
	{
		string repo = await NewRepoAsync("ssh://git@git.example.com:7999/PRJ/widgets.git");
		Environment.SetEnvironmentVariable("STAMPEDED_BITBUCKET_BASE_URL", "https://bitbucket.example.com/bitbucket/");

		var host = await PullRequestHosts.TryForAsync(repo);
		string? prUrl = host is null ? null : await host.PrUrlAsync(17);

		Assert.Multiple(() => {
			Assert.That(host?.Name, Is.EqualTo("Bitbucket Data Center"));
			Assert.That(prUrl, Is.EqualTo("https://bitbucket.example.com/bitbucket/projects/PRJ/repos/widgets/pull-requests/17"));
		});
	}

	async Task<string> NewRepoAsync(string? origin)
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-host-" + Guid.NewGuid().ToString("N"));
		dirs.Add(dir);
		Directory.CreateDirectory(dir);
		await ExternalTool.RunAsync("git", ["init"], dir);
		if (origin is not null)
			await ExternalTool.RunAsync("git", ["remote", "add", "origin", origin], dir);
		return dir;
	}
}
