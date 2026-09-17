using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// Which remote a clone fetches from and pushes to. Not every clone calls it origin: one made
/// with `git clone -o github`, or one whose origin was renamed, has a remote under another
/// name, and reading it as origin used to fail every fetch without saying why.
/// </summary>
public class GitRemoteTests
{
	readonly List<string> temporaryDirectories = [];

	[TearDown]
	public void RemoveTemporaryDirectories()
	{
		foreach (var dir in temporaryDirectories)
		{
			TempDirectory.Delete(dir);
		}
		temporaryDirectories.Clear();
	}

	[Test]
	public void ExplicitDefaultRemoteWins()
		=> Assert.That(GitService.ChooseRemote(["origin", "github"], "github", null), Is.EqualTo("github"));

	[Test]
	public void OriginWinsOverTheTrackedRemote()
		=> Assert.That(GitService.ChooseRemote(["origin", "github"], null, "github"), Is.EqualTo("origin"));

	[Test]
	public void TheOnlyRemoteIsTheOne()
		=> Assert.That(GitService.ChooseRemote(["github"], null, null), Is.EqualTo("github"));

	[Test]
	public void TheCheckedOutBranchDecidesBetweenOthers()
		=> Assert.That(GitService.ChooseRemote(["github", "fork"], null, "fork"), Is.EqualTo("fork"));

	[Test]
	public void ADefaultRemoteThatDoesNotExistIsIgnored()
		=> Assert.That(GitService.ChooseRemote(["github"], "gone", null), Is.EqualTo("github"));

	[Test]
	public void SeveralRemotesAndNothingToChooseByIsNoAnswer()
		=> Assert.That(GitService.ChooseRemote(["github", "fork"], null, null), Is.Null);

	[Test]
	public async Task FetchesAndPushesARemoteNotCalledOrigin()
	{
		var (repo, bare) = await RepositoryWithRemote("github");
		var git = new GitService(repo);

		Assert.That(await git.GetRemoteAsync(), Is.EqualTo("github"));
		await git.FetchAsync();
		Assert.That(await git.GetDefaultBaseAsync(), Is.EqualTo("github/main"));

		await Git(repo, "switch", "--quiet", "-c", "topic");
		await File.WriteAllTextAsync(Path.Combine(repo, "topic.txt"), "topic");
		await Git(repo, "add", "topic.txt");
		await Git(repo, "commit", "--quiet", "-m", "topic");
		var result = await git.PushBranchAsync("topic");

		Assert.That(result.Outcome, Is.EqualTo(PushOutcome.Created));
		Assert.That((await Git(bare, "rev-parse", "topic")).Trim(), Is.EqualTo(result.Sha));
	}

	[Test]
	public async Task AmbiguousRemotesSayHowToChoose()
	{
		var (repo, _) = await RepositoryWithRemote("github");
		await Git(repo, "remote", "add", "fork", NewDirectory());
		await Git(repo, "switch", "--quiet", "--detach");
		var git = new GitService(repo);

		var failure = Assert.ThrowsAsync<ToolFailedException>(async () => await git.FetchAsync());
		Assert.That(ExternalTool.Explain(failure!), Does.Contain("fork, github").And.Contain("checkout.defaultRemote"));

		// The reader follows the advice; the same service sees it without being made again.
		await Git(repo, "config", "checkout.defaultRemote", "github");
		Assert.That(await git.GetRemoteAsync(), Is.EqualTo("github"));
	}

	[Test]
	public void NoRemoteSaysSo()
	{
		string repo = NewDirectory();
		Assert.That(async () => await Git(repo, "init", "--quiet"), Throws.Nothing);

		var failure = Assert.ThrowsAsync<ToolFailedException>(async () => await new GitService(repo).GetRemoteAsync());
		Assert.That(ExternalTool.Explain(failure!), Does.Contain("no git remote"));
	}

	async Task<(string Repo, string Bare)> RepositoryWithRemote(string name)
	{
		string bare = NewDirectory();
		await Git(bare, "init", "--quiet", "--bare", "--initial-branch=main");
		string repo = NewDirectory();
		await Git(repo, "init", "--quiet", "--initial-branch=main");
		await Git(repo, "config", "user.name", "Test");
		await Git(repo, "config", "user.email", "test@example.com");
		await Git(repo, "remote", "add", name, bare);
		await File.WriteAllTextAsync(Path.Combine(repo, "base.txt"), "base");
		await Git(repo, "add", "base.txt");
		await Git(repo, "commit", "--quiet", "-m", "base");
		await Git(repo, "push", "--quiet", name, "main");
		return (repo, bare);
	}

	string NewDirectory()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		temporaryDirectories.Add(dir);
		return dir;
	}

	static Task<string> Git(string dir, params string[] args) => ExternalTool.RunAsync("git", args, dir);
}
