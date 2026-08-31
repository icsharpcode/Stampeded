using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// Whether a branch is already in the default branch. Ancestry answers it for a plain merge;
/// a rebase merge replays the commits, so nothing of the branch survives in the target and
/// only patch equivalence can still recognise it.
/// </summary>
public class GitMergeStateTests
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
		await Commit("base.txt", "base");
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
	public async Task ListsABranchThatIsAnAncestorAndNotOneWithItsOwnCommits()
	{
		await Git("branch", "already-in");
		await Git("checkout", "--quiet", "-b", "ahead");
		await Commit("ahead.txt", "ahead");

		var merged = await new GitService(repo).ListMergedBranchesAsync("main");

		Assert.That(merged, Does.Contain("already-in"));
		Assert.That(merged, Does.Not.Contain("ahead"));
	}

	[Test]
	public async Task RecognisesARebaseMergedBranch()
	{
		await Git("checkout", "--quiet", "-b", "topic");
		await Commit("topic.txt", "on topic");
		await Git("checkout", "--quiet", "main");
		await Replay("topic");

		var git = new GitService(repo);

		Assert.That(await git.ListMergedBranchesAsync("main"), Does.Not.Contain("topic"),
			"ancestry cannot see a rebase merge");
		Assert.That(await git.IsMergedByPatchAsync("topic", "main"), Is.True);
	}

	[Test]
	public async Task DoesNotCallABranchMergedWhenOneCommitIsStillMissing()
	{
		await Git("checkout", "--quiet", "-b", "topic");
		await Commit("first.txt", "first");
		await Commit("second.txt", "second");
		await Git("checkout", "--quiet", "main");
		// Only the first commit was taken over.
		await Replay("topic~1");

		Assert.That(await new GitService(repo).IsMergedByPatchAsync("topic", "main"), Is.False);
	}

	[Test]
	public async Task TreatsABranchWithNoCommitsOfItsOwnAsMerged()
	{
		await Git("branch", "untouched");

		Assert.That(await new GitService(repo).IsMergedByPatchAsync("untouched", "main"), Is.True);
	}

	[Test]
	public async Task DoesNotRecogniseASquashOfSeveralCommits()
	{
		await Git("checkout", "--quiet", "-b", "topic");
		await Commit("first.txt", "first");
		await Commit("second.txt", "second");
		await Git("checkout", "--quiet", "main");
		await Git("merge", "--squash", "topic");
		await Git("commit", "--quiet", "-m", "squashed topic");

		// The squash produced one commit whose patch matches neither of the two, so this
		// says no. Documenting the limit, not endorsing it.
		Assert.That(await new GitService(repo).IsMergedByPatchAsync("topic", "main"), Is.False);
	}

	[Test]
	public async Task DeletesAMergedBranchAndReportsWhereItPointed()
	{
		await Git("branch", "already-in");
		string tip = (await Git("rev-parse", "already-in")).Trim();

		var deletion = await new GitService(repo).DeleteBranchAsync("already-in");

		Assert.That(deletion.Sha, Is.EqualTo(tip));
		Assert.That(deletion.RemovedWorktree, Is.Null);
		Assert.That(await Git("branch", "--format=%(refname:short)"), Does.Not.Contain("already-in"));
	}

	[Test]
	public async Task DeletesARebaseMergedBranchGitDoesNotRecognise()
	{
		await Git("checkout", "--quiet", "-b", "topic");
		await Commit("topic.txt", "on topic");
		await Git("checkout", "--quiet", "main");
		await Replay("topic");

		await new GitService(repo).DeleteBranchAsync("topic");

		Assert.That(await Git("branch", "--format=%(refname:short)"), Does.Not.Contain("topic"));
	}

	[Test]
	public async Task DeletesABranchInTheDefaultBranchWhileHeadLagsBehindIt()
	{
		// The shape `git branch -d` gets wrong. The branch is in the default branch, but it
		// has no upstream, so -d falls back to measuring against HEAD - which is behind - and
		// calls work unmerged that is plainly merged.
		await Git("checkout", "--quiet", "-b", "topic");
		await Commit("topic.txt", "on topic");
		await Git("checkout", "--quiet", "main");
		await Git("merge", "--quiet", "--no-ff", "-m", "merge topic", "topic");
		await Git("branch", "default-branch");
		await Git("reset", "--quiet", "--hard", "HEAD~1");

		Assert.That(await Git("branch", "--merged", "default-branch", "--format=%(refname:short)"),
			Does.Contain("topic"), "it is in the default branch");
		Assert.That(async () => await Git("branch", "-d", "topic"), Throws.InstanceOf<ToolFailedException>(),
			"yet -d refuses, which is the bug this guards");

		await new GitService(repo).DeleteBranchAsync("topic");

		Assert.That(await Git("branch", "--format=%(refname:short)"), Does.Not.Contain("topic"));
	}

	[Test]
	public async Task RemovesTheWorktreeThatHoldsTheBranch()
	{
		await Git("branch", "already-in");
		string worktree = NewDirectory();
		await Git("worktree", "add", "--quiet", worktree, "already-in");
		string worktreeAsGitReportsIt = await AsGitReports(worktree);

		var deletion = await new GitService(repo).DeleteBranchAsync("already-in");

		Assert.That(deletion.RemovedWorktree, Is.EqualTo(worktreeAsGitReportsIt));
		Assert.That(Directory.Exists(worktree), Is.False);
		Assert.That(await Git("branch", "--format=%(refname:short)"), Does.Not.Contain("already-in"));
	}

	[TestCase("modified", Description = "an edit to a tracked file")]
	[TestCase("untracked", Description = "a file git does not know about")]
	public async Task KeepsEverythingWhenTheWorktreeIsNotClean(string kind)
	{
		await Git("branch", "already-in");
		string worktree = NewDirectory();
		await Git("worktree", "add", "--quiet", worktree, "already-in");
		string file = Path.Combine(worktree, kind == "modified" ? "base.txt" : "scratch.txt");
		await File.WriteAllTextAsync(file, "work in progress");

		// Merged says the commits are safe elsewhere. It says nothing about this, so the
		// deletion has to fail as a whole rather than take the directory with it.
		Assert.That(async () => await new GitService(repo).DeleteBranchAsync("already-in"),
			Throws.InstanceOf<ToolFailedException>());

		Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo("work in progress"));
		Assert.That(Directory.Exists(worktree), Is.True);
		Assert.That(await Git("branch", "--format=%(refname:short)"), Does.Contain("already-in"));

		await Git("worktree", "remove", "--force", worktree);
	}

	string NewDirectory()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(dir);
		temporaryDirectories.Add(dir);
		return dir;
	}

	[Test]
	public async Task RemovesAWorktreeWithSubmodulesThatGitRefusesToTouch()
	{
		await AddSubmodule();
		await Git("branch", "already-in");
		string worktree = NewDirectory();
		await Git("worktree", "add", "--quiet", worktree, "already-in");
		string worktreeAsGitReportsIt = await AsGitReports(worktree);

		var deletion = await new GitService(repo).DeleteBranchAsync("already-in");

		Assert.That(deletion.RemovedWorktree, Is.EqualTo(worktreeAsGitReportsIt));
		Assert.That(Directory.Exists(worktree), Is.False);
		Assert.That(await Git("branch", "--format=%(refname:short)"), Does.Not.Contain("already-in"));
		Assert.That(await Git("worktree", "list", "--porcelain"), Does.Not.Contain(worktreeAsGitReportsIt),
			"the administrative entry has to go with the directory");
	}

	[Test]
	public async Task KeepsADirtyWorktreeWithSubmodulesInsteadOfDeletingItOutright()
	{
		await AddSubmodule();
		await Git("branch", "already-in");
		string worktree = NewDirectory();
		await Git("worktree", "add", "--quiet", worktree, "already-in");
		await File.WriteAllTextAsync(Path.Combine(worktree, "scratch.txt"), "work in progress");

		// git tests cleanliness before it refuses on submodules, so a dirty worktree never
		// reaches the fallback that deletes the directory - which is the point: the path that
		// can delete outright is only entered for a worktree git has already found clean.
		Assert.That(async () => await new GitService(repo).DeleteBranchAsync("already-in"),
			Throws.InstanceOf<ToolFailedException>());

		Assert.That(await File.ReadAllTextAsync(Path.Combine(worktree, "scratch.txt")),
			Is.EqualTo("work in progress"));
		Assert.That(Directory.Exists(worktree), Is.True);
		Assert.That(await Git("branch", "--format=%(refname:short)"), Does.Contain("already-in"));

		await Git("worktree", "remove", "--force", worktree);
	}

	/// <summary>Gives the repository a submodule, which is what makes git refuse to remove
	/// any worktree of it.</summary>
	async Task AddSubmodule()
	{
		string sub = NewDirectory();
		await ExternalTool.RunAsync("git", ["init", "--quiet", "--initial-branch=main"], sub);
		await ExternalTool.RunAsync("git", ["config", "user.name", "Test"], sub);
		await ExternalTool.RunAsync("git", ["config", "user.email", "test@example.com"], sub);
		await File.WriteAllTextAsync(Path.Combine(sub, "sub.txt"), "sub");
		await ExternalTool.RunAsync("git", ["add", "sub.txt"], sub);
		await ExternalTool.RunAsync("git", ["commit", "--quiet", "-m", "sub"], sub);
		// Local paths as submodule sources are refused by default since CVE-2022-39253.
		await Git("-c", "protocol.file.allow=always", "submodule", "--quiet", "add", sub, "vendor");
		await Git("commit", "--quiet", "-m", "add submodule");
	}

	/// <summary>Replays a commit onto the current branch the way a rebase merge does: same
	/// patch, different commit. The date is rewritten afterwards because a cherry-pick that
	/// keeps the tree, parent, message and timestamp produces a byte-identical commit object -
	/// git would hand back the very commit being replayed, and the branch would look merged
	/// for the uninteresting reason that it now is.</summary>
	async Task Replay(string commit)
	{
		await Git("cherry-pick", commit);
		await Git("commit", "--quiet", "--amend", "--no-edit", "--date=2020-01-01T00:00:00");
	}

	async Task Commit(string fileName, string content)
	{
		await File.WriteAllTextAsync(Path.Combine(repo, fileName), content);
		await Git("add", fileName);
		await Git("commit", "--quiet", "-m", "add " + fileName);
	}

	Task<string> Git(params string[] args) => ExternalTool.RunAsync("git", args, repo);

	/// <summary>The path in the form git prints it - forward slashes on Windows, symlinks
	/// resolved on macOS - which is the form the service passes on unchanged.</summary>
	static async Task<string> AsGitReports(string dir)
		=> (await ExternalTool.RunAsync("git", ["rev-parse", "--show-toplevel"], dir)).Trim();
}
