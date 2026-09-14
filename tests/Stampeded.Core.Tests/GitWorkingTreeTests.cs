using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// What a checkout contributes to a review beyond its last commit. Untracked files are the
/// case worth pinning: build output, a scratch file and a local config sit next to the work
/// in every real clone, and none of them is part of the change being read.
/// </summary>
public class GitWorkingTreeTests
{
	string repo = "";
	readonly List<string> temporaryDirectories = [];

	[SetUp]
	public async Task CreateRepository()
	{
		repo = NewDirectory();
		await Git("init", "--quiet", "--initial-branch=main");
		await Git("config", "user.name", "Test");
		await Git("config", "user.email", "test@example.com");
		await Write("tracked.txt", "one\n");
		await Git("add", "tracked.txt");
		await Git("commit", "--quiet", "-m", "base");
	}

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
	public async Task DiffsTrackedChangesAndNotUntrackedFiles()
	{
		await Write("tracked.txt", "two\n");
		await Write("untracked.txt", "scratch\n");

		var files = await new GitService(repo).DiffWorkingTreeAsync(repo, "HEAD");

		Assert.That(files.Select(f => f.Path), Is.EqualTo(new[] { "tracked.txt" }));
	}

	[Test]
	public async Task ACheckoutWithOnlyUntrackedFilesIsNotDirty()
	{
		await Write("untracked.txt", "scratch\n");

		Assert.That(await new GitService(repo).IsDirtyAsync(repo), Is.False);
	}

	[Test]
	public async Task ACheckoutWithAModifiedFileIsDirty()
	{
		await Write("tracked.txt", "two\n");

		Assert.That(await new GitService(repo).IsDirtyAsync(repo), Is.True);
	}

	string NewDirectory()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		temporaryDirectories.Add(dir);
		return dir;
	}

	Task Write(string fileName, string content)
		=> File.WriteAllTextAsync(Path.Combine(repo, fileName), content);

	Task<string> Git(params string[] args) => ExternalTool.RunAsync("git", args, repo);
}
