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
			TempDirectory.Delete(dir);
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
		string worktreeAsGitReportsIt = await AsGitReports(worktree);

		var git = new GitService(repo);
		var result = await git.RebaseBranchAsync("topic", "main");

		Assert.That(await MergeBase("topic", "main"), Is.EqualTo(await RevParse("main")),
			"topic should now sit on top of main");
		// The checkout that holds the branch has to move with it, or its index and working
		// tree describe a commit the branch no longer points at.
		Assert.That(result.Checkout, Is.EqualTo(worktreeAsGitReportsIt));
		Assert.That((await Git(worktree, "rev-parse", "HEAD")).Trim(), Is.EqualTo(await RevParse("topic")));
		Assert.That((await Git(worktree, "status", "--porcelain")).Trim(), Is.Empty);
		Assert.That(File.Exists(Path.Combine(worktree, "main.txt")), Is.True,
			"the rebased checkout should have main's file");

		// The recovery the UI offers has to work here, and git branch -f would be refused.
		Assert.That(result.RecoveryCommand("topic"), Is.EqualTo($"git -C {worktreeAsGitReportsIt} reset --hard {result.Before[..9]}"));
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
	[Test]
	public async Task NothingIsInProgressInAQuietRepository()
	{
		Assert.That(await new GitService(repo).ListInProgressAsync(), Is.Empty);
	}

	[Test]
	public async Task AConflictedRebaseIsReportedAsInProgress()
	{
		// Both sides change base.txt, so replaying topic onto main conflicts.
		await Git(repo, "checkout", "--quiet", "topic");
		await Commit("base.txt", "topic edit");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("base.txt", "main edit");
		await ConfigureMergeTool("false");
		var git = new GitService(repo);
		var result = await git.RebaseBranchAsync("topic", "main");
		Assert.That(result.Outcome, Is.EqualTo(RebaseOutcome.Conflicted));

		var pending = await git.ListInProgressAsync();

		Assert.That(pending, Has.Count.EqualTo(1));
		Assert.That(pending[0].Kind, Is.EqualTo(GitOperation.Rebase));
		Assert.That(pending[0].Describe, Does.Contain("rebase").IgnoreCase);
	}

	[Test]
	public async Task AbortingPutsTheBranchBackAndTakesTheScratchCheckoutWithIt()
	{
		// Both sides change base.txt, so replaying topic onto main conflicts.
		await Git(repo, "checkout", "--quiet", "topic");
		await Commit("base.txt", "topic edit");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("base.txt", "main edit");
		await ConfigureMergeTool("false");
		var git = new GitService(repo);
		string before = await RevParse("topic");
		var result = await git.RebaseBranchAsync("topic", "main");
		var pending = await git.ListInProgressAsync();

		await git.AbortAsync(pending[0]);

		Assert.That(await git.ListInProgressAsync(), Is.Empty, "nothing is half-finished any more");
		Assert.That(await RevParse("topic"), Is.EqualTo(before), "the branch is back where it started");
		Assert.That(Directory.Exists(result.WorkingDirectory), Is.False,
			"a checkout that existed only for the rebase goes with it rather than being left behind");
	}

	[Test]
	public async Task AbortingAConflictedMergeLeavesTheCheckoutUsable()
	{
		// Not something this tool starts, but a reader who merged by hand in their own clone is
		// stuck the same way, and the button has to reach that too.
		await Git(repo, "checkout", "--quiet", "topic");
		await Commit("base.txt", "topic edit");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("base.txt", "main edit");
		Assert.That(async () => await Git(repo, "merge", "topic"),
			Throws.InstanceOf<ToolFailedException>());
		var git = new GitService(repo);

		var pending = await git.ListInProgressAsync();
		Assert.That(pending.Select(o => o.Kind), Is.EqualTo(new[] { GitOperation.Merge }));

		await git.AbortAsync(pending[0]);

		Assert.That(await git.ListInProgressAsync(), Is.Empty);
		Assert.That((await Git(repo, "status", "--porcelain")).Trim(), Is.Empty);
	}

	[Test]
	public async Task RefusesToRebaseOverSomethingAlreadyHalfFinished()
	{
		// The state a cancelled merge tool leaves behind. Retrying used to reach git, which
		// refused with a fatal about a rebase-merge directory and never ran the merge tool -
		// so the rebase looked like it had quietly done nothing.
		await Git(repo, "checkout", "--quiet", "topic");
		await Commit("base.txt", "topic edit");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("base.txt", "main edit");
		await Git(repo, "checkout", "--quiet", "topic");
		Assert.That(async () => await Git(repo, "rebase", "main"),
			Throws.InstanceOf<ToolFailedException>());
		var git = new GitService(repo);
		Assert.That(await git.InProgressInAsync(repo), Is.EqualTo(GitOperation.Rebase));

		Assert.That(async () => await git.RebaseBranchAsync("topic", "main"),
			Throws.InstanceOf<RefusedException>().With.Message.Contains("already in progress"));

		await Git(repo, "rebase", "--abort");
	}

	[Test]
	public async Task WhatIsConflictedMustBeResolvedBeforeItCanBeContinued()
	{
		await ConflictedRebaseInPlace();
		var git = new GitService(repo);

		var stopped = (await git.ListInProgressAsync())[0];

		Assert.That(stopped.Unmerged, Is.EqualTo(1));
		Assert.That(stopped.CanResolve, Is.True);
		Assert.That(stopped.CanContinue, Is.False, "git refuses a continue while a file is conflicted");
		Assert.That(stopped.CanSkip, Is.True);
		Assert.That(stopped.Headline, Is.EqualTo("Rebase of topic - 1 file conflicted"));

		// Resolving is what opens the way to continuing, and nothing else does.
		await ConfigureMergeToolTaking("resolved");
		await git.RunMergeToolAsync(stopped);

		var resolved = (await git.ListInProgressAsync())[0];
		Assert.That(resolved.Unmerged, Is.Zero);
		Assert.That(resolved.CanContinue, Is.True);
		Assert.That(resolved.CanResolve, Is.False);

		await git.ContinueAsync(resolved);
		Assert.That(await git.ListInProgressAsync(), Is.Empty, "continuing finished it");
		Assert.That((await Git(repo, "show", "topic:base.txt")).Trim(), Is.EqualTo("resolved"));
	}

	[Test]
	public async Task SkippingDropsTheStepAndFinishesTheRebase()
	{
		await ConflictedRebaseInPlace();
		var git = new GitService(repo);

		await git.SkipAsync((await git.ListInProgressAsync())[0]);

		Assert.That(await git.ListInProgressAsync(), Is.Empty);
		Assert.That((await Git(repo, "show", "topic:base.txt")).Trim(), Is.EqualTo("main edit"),
			"the skipped commit's change is not applied");
	}

	/// <summary>Leaves topic mid-rebase in the repository's own checkout, the way a merge tool
	/// the reader cancelled out of does.</summary>
	async Task ConflictedRebaseInPlace()
	{
		await Git(repo, "checkout", "--quiet", "topic");
		await Commit("base.txt", "topic edit");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("base.txt", "main edit");
		await Git(repo, "checkout", "--quiet", "topic");
		Assert.That(async () => await Git(repo, "rebase", "main"),
			Throws.InstanceOf<ToolFailedException>());
	}

	[Test]
	public async Task APausedOperationReadsDifferentlyFromAConflictedOne()
	{
		await ConflictedRebaseInPlace();
		var git = new GitService(repo);

		Assert.That((await git.ListInProgressAsync())[0].Headline, Does.Contain("conflicted"));

		await ConfigureMergeToolTaking("resolved");
		await git.RunMergeToolAsync((await git.ListInProgressAsync())[0]);

		var paused = (await git.ListInProgressAsync())[0];
		Assert.That(paused.Headline, Does.Contain("paused"),
			"stopped part-way is a note, not an alarm - the banner colours itself from this");
		Assert.That(paused.Unmerged, Is.Zero);

		await Git(repo, "rebase", "--abort");
	}

	[Test]
	public async Task TheStateIsFoundInALinkedWorktreeWithoutAskingGit()
	{
		// The admin directory is read off disk rather than through rev-parse, because a
		// repository with forty worktrees would otherwise cost forty processes per check.
		string other = NewDirectory();
		await Git(repo, "worktree", "add", "--quiet", other, "topic");
		string otherAsGitReportsIt = await AsGitReports(other);
		var git = new GitService(repo);
		Assert.That(await git.InProgressInAsync(other), Is.Null);

		await Git(other, "checkout", "--quiet", "-b", "wt-topic");
		await File.WriteAllTextAsync(Path.Combine(other, "base.txt"), "worktree edit");
		await Git(other, "commit", "--quiet", "-am", "worktree edit");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("base.txt", "main edit");
		Assert.That(async () => await Git(other, "rebase", "main"),
			Throws.InstanceOf<ToolFailedException>());

		Assert.That(await git.InProgressInAsync(other), Is.EqualTo(GitOperation.Rebase));
		var found = await git.ListInProgressAsync();
		Assert.That(found.Select(o => o.WorkingDirectory), Does.Contain(otherAsGitReportsIt));
		Assert.That(found.Single(o => o.WorkingDirectory == otherAsGitReportsIt).Branch, Is.EqualTo("wt-topic"),
			"the branch is read from the rebase state, since the worktree is detached while it runs");

		await Git(other, "rebase", "--abort");
		await Git(repo, "worktree", "remove", "--force", other);
	}

	[Test]
	public async Task WillNotCommitWhatTheMergeToolLeftUnresolved()
	{
		// An editor the reader closed without deciding: it exits cleanly and touches the file,
		// which is all git asks before calling it resolved. Continuing on that word committed
		// the conflict markers themselves and reported the rebase a success.
		await Git(repo, "checkout", "--quiet", "topic");
		await Commit("base.txt", "topic edit");
		await Git(repo, "checkout", "--quiet", "main");
		await Commit("base.txt", "main edit");
		await ConfigureMergeTool("touch \"$MERGED\"");

		var git = new GitService(repo);
		var result = await git.RebaseBranchAsync("topic", "main");

		Assert.That(result.Outcome, Is.EqualTo(RebaseOutcome.Conflicted),
			"a file that still carries markers is not a resolved file");
		Assert.That((await Git(repo, "show", "topic:base.txt")), Does.Not.Contain("<<<<<<<"),
			"nothing with conflict markers in it may reach the branch");
		temporaryDirectories.Add(result.WorkingDirectory);
		await Git(result.WorkingDirectory, "rebase", "--abort");
		await Git(repo, "worktree", "remove", "--force", result.WorkingDirectory);
	}

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

	/// <summary>The path in the form git prints it - forward slashes on Windows, symlinks
	/// resolved on macOS - which is the form the service passes on unchanged.</summary>
	static async Task<string> AsGitReports(string dir)
		=> (await ExternalTool.RunAsync("git", ["rev-parse", "--show-toplevel"], dir)).Trim();

	async Task<string> RevParse(string reference) => (await Git(repo, "rev-parse", reference)).Trim();

	async Task<string> MergeBase(string a, string b) => (await Git(repo, "merge-base", a, b)).Trim();
}
