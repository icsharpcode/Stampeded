using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// The diff a reader wants after a force-push: what the author changed, and not the commits
/// the rebase brought in with the new base. Real repositories, because the interesting
/// behaviour is git's - a merge whose base is one revision and whose sides are another.
/// </summary>
public class GitInterdiffTests
{
	string repo = "";
	readonly List<string> temporaryDirectories = [];

	[SetUp]
	public async Task CreateRepository()
	{
		repo = NewDirectory();
		await Git(repo, "init", "--quiet", "--initial-branch=main");
		await Git(repo, "config", "user.name", "Test");
		await Git(repo, "config", "user.email", "test@example.com");
		await Commit("base.txt", "base\n");
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

	/// <summary>
	/// The reviewed pass, then a rebase onto an upstream commit plus an edit of the author's
	/// own - the shape of every force-push worth reading.
	/// </summary>
	async Task<(string OldBase, string OldHead, string NewBase, string NewHead)> RebasedWithUpstreamWork()
	{
		string oldBase = await RevParse("HEAD");
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await Commit("feature.cs", "class Feature { int One() => 1; }\n");
		string oldHead = await RevParse("HEAD");

		await Git(repo, "checkout", "--quiet", "main");
		await Commit("upstream.cs", "class Upstream { }\n");
		string newBase = await RevParse("HEAD");

		await Git(repo, "checkout", "--quiet", "topic");
		await Git(repo, "rebase", "--quiet", "main");
		await File.WriteAllTextAsync(Path.Combine(repo, "feature.cs"), "class Feature { int Two() => 2; }\n");
		await Git(repo, "commit", "--quiet", "--all", "--amend", "-m", "add feature.cs");
		return (oldBase, oldHead, newBase, await RevParse("HEAD"));
	}

	[Test]
	public async Task TheReplayHidesTheCommitsARebaseBroughtIn()
	{
		var (oldBase, oldHead, newBase, newHead) = await RebasedWithUpstreamWork();
		var git = new GitService(repo);

		string? tree = await git.ReplayTreeAsync(newBase, oldHead, oldBase);

		Assert.That(tree, Is.Not.Null, "the work replays onto the new base without conflicts");
		var files = await git.DiffAsync(tree!, newHead);
		Assert.That(files.Select(f => f.Path), Is.EquivalentTo(new[] { "feature.cs" }));
	}

	[Test]
	public async Task ThePlainInterdiffShowsWhatTheReplayHides()
	{
		// The control for the test above: without the replay, the rebase's upstream commit is
		// in the diff, which is the whole reason merge-tree is used.
		var (_, oldHead, _, newHead) = await RebasedWithUpstreamWork();
		var git = new GitService(repo);

		var files = await git.DiffAsync(oldHead, newHead);

		Assert.That(files.Select(f => f.Path), Does.Contain("upstream.cs"));
	}

	[Test]
	public async Task AnAmendWithoutARebaseReplaysToThePlainInterdiff()
	{
		string baseSha = await RevParse("HEAD");
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await Commit("feature.cs", "one\n");
		string oldHead = await RevParse("HEAD");
		await File.WriteAllTextAsync(Path.Combine(repo, "feature.cs"), "two\n");
		await Git(repo, "commit", "--quiet", "--all", "--amend", "-m", "add feature.cs");
		string newHead = await RevParse("HEAD");
		var git = new GitService(repo);

		string? tree = await git.ReplayTreeAsync(baseSha, oldHead, baseSha);
		var replayed = await git.DiffAsync(tree!, newHead);
		var plain = await git.DiffAsync(oldHead, newHead);

		Assert.That(replayed.Select(f => f.Path), Is.EquivalentTo(plain.Select(f => f.Path)));
	}

	[Test]
	public async Task WorkThatCannotBeReplayedAnswersWithNull()
	{
		string oldBase = await RevParse("HEAD");
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await File.WriteAllTextAsync(Path.Combine(repo, "base.txt"), "the author's line\n");
		await Git(repo, "commit", "--quiet", "--all", "-m", "topic edit");
		string oldHead = await RevParse("HEAD");

		await Git(repo, "checkout", "--quiet", "main");
		await File.WriteAllTextAsync(Path.Combine(repo, "base.txt"), "somebody else's line\n");
		await Git(repo, "commit", "--quiet", "--all", "-m", "upstream edit");
		string newBase = await RevParse("HEAD");
		var git = new GitService(repo);

		string? tree = await git.ReplayTreeAsync(newBase, oldHead, oldBase);

		Assert.That(tree, Is.Null, "a conflicted replay has no tree that is the author's work");
	}

	[Test]
	public async Task AncestryTellsAddedCommitsFromRewrittenOnes()
	{
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await Commit("feature.cs", "one\n");
		string first = await RevParse("HEAD");
		await Commit("second.cs", "two\n");
		string added = await RevParse("HEAD");
		var git = new GitService(repo);

		Assert.That(await git.IsAncestorAsync(first, added), Is.True, "commits added on top");

		await Git(repo, "commit", "--quiet", "--amend", "-m", "rewritten");
		string rewritten = await RevParse("HEAD");

		Assert.That(await git.IsAncestorAsync(first, rewritten), Is.True, "the first commit is still there");
		Assert.That(await git.IsAncestorAsync(added, rewritten), Is.False, "the amended one is not");
	}

	[Test]
	public async Task APinnedHeadSurvivesAForcedRefUpdateAndCollection()
	{
		// What a force-push does to the head a reader compared against: the ref that named it
		// is moved, and nothing else in the repository mentions it.
		await Git(repo, "checkout", "--quiet", "-b", "topic");
		await Commit("feature.cs", "one\n");
		string reviewed = await RevParse("HEAD");
		await Git(repo, "update-ref", "refs/stampeded/pr/1", reviewed);
		var git = new GitService(repo);
		await git.PinReviewHeadsAsync("pr/1", reviewed, null);

		await File.WriteAllTextAsync(Path.Combine(repo, "feature.cs"), "two\n");
		await Git(repo, "commit", "--quiet", "--all", "--amend", "-m", "rewritten");
		await Git(repo, "update-ref", "refs/stampeded/pr/1", await RevParse("HEAD"));
		await Git(repo, "reflog", "expire", "--expire=now", "--expire-unreachable=now", "--all");
		await Git(repo, "gc", "--quiet", "--prune=now");

		Assert.That(await git.HasCommitAsync(reviewed), Is.True);
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
}
