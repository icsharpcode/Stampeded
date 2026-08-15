using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// What the worktree cache keeps. Real worktrees of a real repository, because what is being
/// checked is which directories survive and whether git still agrees they exist.
/// </summary>
public class WorktreeCacheTests
{
	string repo = "";
	string cacheRoot = "";
	readonly List<string> temporaryDirectories = [];

	[SetUp]
	public async Task CreateRepository()
	{
		repo = NewDirectory();
		cacheRoot = NewDirectory();
		// The manager reads the cache root from the environment, so a test can be given one
		// of its own rather than sharing the user's.
		Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cacheRoot);
		await Git("init", "--quiet", "--initial-branch=main");
		await Git("config", "user.name", "Test");
		await Git("config", "user.email", "test@example.com");
	}

	[TearDown]
	public void RemoveTemporaryDirectories()
	{
		Environment.SetEnvironmentVariable("XDG_CACHE_HOME", null);
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
	public async Task KeepsWhatIsInUseAndTheMostRecentlyUsedOthers()
	{
		var manager = new WorktreeManager(repo);
		var shas = new List<string>();
		for (int i = 0; i < 5; i++)
		{
			await Commit($"file{i}.txt", $"{i}\n");
			string sha = await RevParse("HEAD");
			shas.Add(sha);
			string dir = await manager.GetOrCreateAsync(sha);
			// Ordered in time, so "most recent" means something to assert against.
			Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddMinutes(i));
		}

		// The oldest is the one in use, so it survives on its own account rather than by age.
		int removed = await manager.PruneToRecentAsync([shas[0]], recent: 2);

		Assert.That(removed, Is.EqualTo(2), "five worktrees, one pinned and two kept by age");
		Assert.That(Cached(), Is.EquivalentTo(new[] { shas[0][..9], shas[3][..9], shas[4][..9] }));
	}

	[Test]
	public async Task ReusingAWorktreeCountsAsUsingIt()
	{
		var manager = new WorktreeManager(repo);
		await Commit("a.txt", "a\n");
		string older = await RevParse("HEAD");
		string olderDir = await manager.GetOrCreateAsync(older);
		Directory.SetLastWriteTimeUtc(olderDir, DateTime.UtcNow.AddHours(-2));
		await Commit("b.txt", "b\n");
		string newer = await RevParse("HEAD");
		await manager.GetOrCreateAsync(newer);

		// Opening the older review again is what makes it the recent one.
		await manager.GetOrCreateAsync(older);
		await manager.PruneToRecentAsync([], recent: 1);

		Assert.That(Cached(), Is.EquivalentTo(new[] { older[..9] }));
	}

	[Test]
	public async Task LeavesGitWithNoStaleRegistrations()
	{
		var manager = new WorktreeManager(repo);
		await Commit("a.txt", "a\n");
		string sha = await RevParse("HEAD");
		await manager.GetOrCreateAsync(sha);

		await manager.PruneToRecentAsync([], recent: 0);

		Assert.That(Cached(), Is.Empty);
		string listed = await Git("worktree", "list", "--porcelain");
		Assert.That(listed, Does.Not.Contain(cacheRoot), "git no longer believes the worktree is there");
	}

	IEnumerable<string> Cached()
	{
		string dir = Path.Combine(cacheRoot, "stampeded", "worktrees", Path.GetFileName(repo));
		return Directory.Exists(dir)
			? Directory.EnumerateDirectories(dir).Select(Path.GetFileName).OfType<string>()
			: [];
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
		await Git("add", fileName);
		await Git("commit", "--quiet", "-m", "add " + fileName);
	}

	Task<string> Git(params string[] args) => ExternalTool.RunAsync("git", args, repo);

	async Task<string> RevParse(string reference) => (await Git("rev-parse", reference)).Trim();
}
