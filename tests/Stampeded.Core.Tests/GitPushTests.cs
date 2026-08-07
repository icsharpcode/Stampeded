using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// Push against a real bare origin. The case that decides the design is a branch that was
/// rebased: origin's copy is no longer an ancestor of it, so a plain push can only be
/// rejected and the force has to be chosen for the user.
/// </summary>
public class GitPushTests
{
	string repo = "";
	string origin = "";
	readonly List<string> temporaryDirectories = [];

	[SetUp]
	public async Task CreateRepositoryWithOrigin()
	{
		origin = NewDirectory();
		await Git(origin, "init", "--quiet", "--bare", "--initial-branch=main");
		repo = NewDirectory();
		await Git(repo, "init", "--quiet", "--initial-branch=main");
		await Git(repo, "config", "user.name", "Test");
		await Git(repo, "config", "user.email", "test@example.com");
		await Git(repo, "remote", "add", "origin", origin);
		await Commit("base.txt", "base");
		await Git(repo, "push", "--quiet", "origin", "main");
	}

	[TearDown]
	public void RemoveTemporaryDirectories()
	{
		foreach (var dir in temporaryDirectories)
		{
			try
			{
				Directory.Delete(dir, recursive: true);
			}
			catch (IOException)
			{
			}
		}
		temporaryDirectories.Clear();
	}

	[Test]
	public async Task CreatesABranchOriginDoesNotHave()
	{
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await Commit("topic.txt", "on topic");

		var result = await new GitService(repo).PushBranchAsync("topic");

		Assert.That(result.Outcome, Is.EqualTo(PushOutcome.Created));
		Assert.That(await OriginSha("topic"), Is.EqualTo(await RevParse("topic")));
	}

	[Test]
	public async Task PushesNewCommitsWithoutForcing()
	{
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await Commit("topic.txt", "on topic");
		await Git(repo, "push", "--quiet", "origin", "topic");
		await Commit("more.txt", "more");

		var result = await new GitService(repo).PushBranchAsync("topic");

		Assert.That(result.Outcome, Is.EqualTo(PushOutcome.Pushed));
		Assert.That(await OriginSha("topic"), Is.EqualTo(await RevParse("topic")));
	}

	[Test]
	public async Task ReportsABranchOriginAlreadyMatches()
	{
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await Commit("topic.txt", "on topic");
		await Git(repo, "push", "--quiet", "origin", "topic");

		var result = await new GitService(repo).PushBranchAsync("topic");

		Assert.That(result.Outcome, Is.EqualTo(PushOutcome.AlreadyUpToDate));
	}

	[Test]
	public async Task ForcePushesARebasedBranch()
	{
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await Commit("topic.txt", "on topic");
		await Git(repo, "push", "--quiet", "origin", "topic");
		string beforeRebase = await RevParse("topic");
		// main moves and topic is replayed onto it, so origin's copy is now unreachable.
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("main.txt", "on main");
		await Git(repo, "checkout", "--quiet", "topic");
		await Git(repo, "rebase", "main");

		var result = await new GitService(repo).PushBranchAsync("topic");

		Assert.That(result.Outcome, Is.EqualTo(PushOutcome.ForcePushed));
		Assert.That(await OriginSha("topic"), Is.EqualTo(await RevParse("topic")));
		Assert.That(await OriginSha("topic"), Is.Not.EqualTo(beforeRebase));
	}

	[Test]
	public async Task RefusesToForcePushOverCommitsItHasNeverSeen()
	{
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await Commit("topic.txt", "on topic");
		await Git(repo, "push", "--quiet", "origin", "topic");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("main.txt", "on main");
		await Git(repo, "checkout", "--quiet", "topic");
		await Git(repo, "rebase", "main");
		// Someone else pushes to the branch, and this clone has not fetched it. The lease is
		// what keeps the force from discarding that commit.
		string other = NewDirectory();
		await Git(other, "clone", "--quiet", origin, other);
		await Git(other, "config", "user.name", "Other");
		await Git(other, "config", "user.email", "other@example.com");
		await Git(other, "checkout", "--quiet", "topic");
		await File.WriteAllTextAsync(Path.Combine(other, "theirs.txt"), "theirs");
		await Git(other, "add", "theirs.txt");
		await Git(other, "commit", "--quiet", "-m", "theirs");
		await Git(other, "push", "--quiet", "origin", "topic");
		string theirs = (await Git(other, "rev-parse", "HEAD")).Trim();

		Assert.That(async () => await new GitService(repo).PushBranchAsync("topic"),
			Throws.InstanceOf<ToolFailedException>());
		Assert.That(await OriginSha("topic"), Is.EqualTo(theirs), "their commit must survive");
	}

	string NewDirectory()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		temporaryDirectories.Add(dir);
		return dir;
	}

	async Task Commit(string fileName, string content)
	{
		await File.WriteAllTextAsync(Path.Combine(repo, fileName), content);
		await Git(repo, "add", fileName);
		await Git(repo, "commit", "--quiet", "-m", "add " + fileName);
	}

	Task<string> Git(string dir, params string[] args) => ExternalTool.RunAsync("git", args, dir);

	async Task<string> RevParse(string reference) => (await Git(repo, "rev-parse", reference)).Trim();

	async Task<string> OriginSha(string branch) => (await Git(origin, "rev-parse", branch)).Trim();
}
