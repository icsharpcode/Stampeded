using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// Rebase against a real repository. The interesting case is a branch that some checkout
/// already has: git allows a branch in only one checkout at a time, so the throwaway
/// worktree the rebase would otherwise use cannot have it.
/// </summary>
public class GitRebaseTests
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
		await Commit("base.txt", "base");
		await Git(repo, "branch", "topic");
		// main moves on, so topic has something to be rebased onto.
		await Commit("main.txt", "on main");
		await Git(repo, "checkout", "--quiet", "topic");
		await Commit("topic.txt", "on topic");
		await Git(repo, "checkout", "--quiet", "main");
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
	public async Task RebasesABranchThatNoCheckoutHas()
	{
		var git = new GitService(repo);
		var result = await git.RebaseBranchAsync("topic", "main");

		Assert.That(result.Before, Is.EqualTo(await RevParse("topic@{1}")));
		Assert.That(result.Checkout, Is.Null);
		Assert.That(result.Outcome, Is.EqualTo(RebaseOutcome.Rebased));
		Assert.That(await MergeBase("topic", "main"), Is.EqualTo(await RevParse("main")),
			"topic should now sit on top of main");
		// git branch -f only works while no checkout has the branch.
		Assert.That(result.RecoveryCommand("topic"), Does.StartWith("git branch -f topic "));
	}

	[Test]
	public async Task RebasesABranchThatIsCheckedOutInAWorktree()
	{
		string worktree = NewDirectory();
		await Git(repo, "worktree", "add", "--quiet", worktree, "topic");

		var git = new GitService(repo);
		var result = await git.RebaseBranchAsync("topic", "main");

		Assert.That(await MergeBase("topic", "main"), Is.EqualTo(await RevParse("main")),
			"topic should now sit on top of main");
		// The checkout that holds the branch has to move with it, or its index and working
		// tree describe a commit the branch no longer points at.
		Assert.That(result.Checkout, Is.EqualTo(worktree));
		Assert.That((await Git(worktree, "rev-parse", "HEAD")).Trim(), Is.EqualTo(await RevParse("topic")));
		Assert.That((await Git(worktree, "status", "--porcelain")).Trim(), Is.Empty);
		Assert.That(File.Exists(Path.Combine(worktree, "main.txt")), Is.True,
			"the rebased checkout should have main's file");

		// The recovery the UI offers has to work here, and git branch -f would be refused.
		Assert.That(result.RecoveryCommand("topic"), Is.EqualTo($"git -C {worktree} reset --hard {result.Before[..9]}"));
		await Git(worktree, "reset", "--hard", result.Before);
		Assert.That(await RevParse("topic"), Is.EqualTo(result.Before));
	}

	[Test]
	public async Task LeavesTheBranchAloneWhenTheCheckoutThatHasItIsDirty()
	{
		string worktree = NewDirectory();
		await Git(repo, "worktree", "add", "--quiet", worktree, "topic");
		await File.WriteAllTextAsync(Path.Combine(worktree, "topic.txt"), "uncommitted edit");

		var git = new GitService(repo);
		string topicBefore = await RevParse("topic");

		Assert.That(async () => await git.RebaseBranchAsync("topic", "main"),
			Throws.InstanceOf<ToolFailedException>());
		Assert.That(await RevParse("topic"), Is.EqualTo(topicBefore));
		Assert.That(await File.ReadAllTextAsync(Path.Combine(worktree, "topic.txt")),
			Is.EqualTo("uncommitted edit"));
	}

	[Test]
	public async Task RunsTheMergeToolOnConflictsAndContinuesTheRebase()
	{
		// Both sides change base.txt, so replaying topic onto main conflicts.
		await Git(repo, "checkout", "--quiet", "topic");
		await Commit("base.txt", "topic edit");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("base.txt", "main edit");
		await ConfigureMergeToolTaking("topic edit");

		var git = new GitService(repo);
		var result = await git.RebaseBranchAsync("topic", "main");

		Assert.That(result.Outcome, Is.EqualTo(RebaseOutcome.Rebased));
		Assert.That(await MergeBase("topic", "main"), Is.EqualTo(await RevParse("main")),
			"topic should now sit on top of main");
		Assert.That((await Git(repo, "show", "topic:base.txt")).Trim(), Is.EqualTo("topic edit"),
			"the merge tool's resolution should be what got committed");
	}

	[Test]
	public async Task LeavesTheRebaseInProgressWhenTheMergeToolResolvesNothing()
	{
		await Git(repo, "checkout", "--quiet", "topic");
		await Commit("base.txt", "topic edit");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("base.txt", "main edit");
		// A merge tool that reports it resolved nothing, as cancelling out of one does.
		await ConfigureMergeTool("false");

		var git = new GitService(repo);
		var result = await git.RebaseBranchAsync("topic", "main");

		Assert.That(result.Outcome, Is.EqualTo(RebaseOutcome.Conflicted));
		Assert.That(Directory.Exists(result.WorkingDirectory), Is.True,
			"the worktree has to survive so the rebase can be finished by hand");
		Assert.That((await Git(result.WorkingDirectory, "status", "--porcelain")).Trim(), Is.Not.Empty);
		temporaryDirectories.Add(result.WorkingDirectory);
		await Git(result.WorkingDirectory, "rebase", "--abort");
		await Git(repo, "worktree", "remove", "--force", result.WorkingDirectory);
	}

	/// <summary>Points merge.tool at a command that resolves every conflict by writing
	/// <paramref name="content"/>, so a conflicted rebase runs without anything interactive.
	/// Git runs these through a shell, which it ships on every platform.</summary>
	Task ConfigureMergeToolTaking(string content)
		=> ConfigureMergeTool($"printf '%s\\n' '{content}' > \"$MERGED\"");

	async Task ConfigureMergeTool(string command)
	{
		await Git(repo, "config", "merge.tool", "stub");
		await Git(repo, "config", "mergetool.stub.cmd", command);
		await Git(repo, "config", "mergetool.stub.trustExitCode", "true");
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

	async Task<string> MergeBase(string a, string b) => (await Git(repo, "merge-base", a, b)).Trim();
}
