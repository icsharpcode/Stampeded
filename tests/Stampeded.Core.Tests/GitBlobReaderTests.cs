using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// Reading file content out of the object database, which is what stands in for a checkout
/// of every revision a review compares.
/// </summary>
public class GitBlobReaderTests
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
	public async Task ReadsAFileAtTheRevisionItIsAskedFor()
	{
		await Commit("a.cs", "first\n");
		string first = await RevParse("HEAD");
		await Commit("a.cs", "second\n");
		using var reader = new GitBlobReader(repo);

		Assert.That(await reader.ReadAsync(first, "a.cs"), Is.EqualTo("first\n"));
		Assert.That(await reader.ReadAsync("HEAD", "a.cs"), Is.EqualTo("second\n"));
	}

	[Test]
	public async Task AnswersNullForWhatARevisionDoesNotHave()
	{
		// The revision before a file was added has no answer to give, which is how a file the
		// change adds is told from one it edits.
		await Commit("a.cs", "one\n");
		string before = await RevParse("HEAD");
		await Commit("b.cs", "two\n");
		using var reader = new GitBlobReader(repo);

		Assert.That(await reader.ReadAsync(before, "b.cs"), Is.Null);
		Assert.That(await reader.ReadAsync(before, "never-existed.cs"), Is.Null);
	}

	[Test]
	public async Task KeepsAnsweringAcrossManyReadsOnOneProcess()
	{
		// The whole point of the batch: one process, many files, no per-file cost.
		for (int i = 0; i < 30; i++)
			await Commit($"file{i}.cs", $"content {i}\n");
		using var reader = new GitBlobReader(repo);

		for (int i = 0; i < 30; i++)
			Assert.That(await reader.ReadAsync("HEAD", $"file{i}.cs"), Is.EqualTo($"content {i}\n"));
	}

	[Test]
	public async Task ReadsAFileWhoseContentIsNotOneLine()
	{
		await Commit("a.cs", "class C\n{\n\tvoid M() { }\n}\n");
		using var reader = new GitBlobReader(repo);

		Assert.That(await reader.ReadAsync("HEAD", "a.cs"), Is.EqualTo("class C\n{\n\tvoid M() { }\n}\n"));
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
