using NUnit.Framework;

using Stampeded.Core.Git;
using Stampeded.Core.GitHub;
using Stampeded.Core.Infra;
using Stampeded.Core.MergeQueue;

namespace Stampeded.Core.Tests;

/// <summary>
/// The queue against a real bare origin and two clones, because the whole design rests on what
/// the server does when two clients write at once: git's refusal to move a ref to a commit that
/// does not descend from the one it holds is the only lock there is. A test that mocked the push
/// would be asserting the belief rather than the behaviour.
/// </summary>
public class MergeQueueTests
{
	string origin = "";
	string alice = "";
	string bob = "";
	readonly List<string> temporaryDirectories = [];

	[SetUp]
	public async Task CreateRepositoryWithTwoClones()
	{
		origin = NewDirectory();
		await Git(origin, "init", "--quiet", "--bare", "--initial-branch=main");
		alice = await Clone("Alice");
		await File.WriteAllTextAsync(Path.Combine(alice, "base.txt"), "base");
		await Git(alice, "add", "base.txt");
		await Git(alice, "commit", "--quiet", "-m", "base");
		await Git(alice, "push", "--quiet", "origin", "main");
		bob = await Clone("Bob");
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
	public async Task AQueueNobodyHasWrittenReadsAsEmpty()
	{
		var snapshot = await Queue(alice).ReadAsync();

		Assert.That(snapshot.Sha, Is.Null);
		Assert.That(snapshot.Document.Entries, Is.Empty);
	}

	[Test]
	public async Task WhatOneClientQueuesAnotherCloneSees()
	{
		await Queue(alice).EnqueueAsync(142, "Fix blame gutter", Head, "squash");

		var document = (await Queue(bob).ReadAsync()).Document;

		Assert.That(document.Entries.Select(e => e.Pr), Is.EqualTo(new[] { 142 }));
		Assert.That(document.Entries[0].Title, Is.EqualTo("Fix blame gutter"));
		Assert.That(document.Entries[0].Method, Is.EqualTo("squash"));
		Assert.That(document.Entries[0].By, Is.EqualTo("alice@host"));
	}

	[Test]
	public async Task QueueingTheSamePullRequestTwiceLeavesOneEntry()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(142, "A change", Head, "squash");
		await queue.EnqueueAsync(142, "A change", Head, "merge");

		Assert.That((await queue.ReadAsync()).Document.Entries, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task NeitherOfTwoClientsWritingAtOnceLosesItsEntry()
	{
		var queue = Queue(alice);
		bool interfered = false;

		// Bob publishes in the window between Alice's read and Alice's push - the race the whole
		// design exists for. Alice's push cannot fast-forward, so the server rejects it and the
		// edit is re-applied to what Bob left behind.
		await queue.UpdateAsync(document => {
			if (!interfered)
			{
				interfered = true;
				Queue(bob).EnqueueAsync(2, "bob's", Head, "merge").GetAwaiter().GetResult();
			}
			return (document with {
				Entries = [.. document.Entries, new MergeQueueEntry(1, "alice's", "aaa", "squash", "alice@host", DateTimeOffset.UtcNow)],
			}, "enqueue #1 by alice@host");
		});

		var entries = (await Queue(bob).ReadAsync()).Document.Entries;
		Assert.That(entries.Select(e => e.Pr), Is.EqualTo(new[] { 2, 1 }),
			"the loser of the race re-applies onto the winner's state, it does not overwrite it");
	}

	[Test]
	public async Task TwoClientsCreatingTheQueueAtOnceLeaveOneQueue()
	{
		var queue = Queue(alice);
		bool interfered = false;

		await queue.UpdateAsync(document => {
			if (!interfered)
			{
				interfered = true;
				Queue(bob).EnqueueAsync(2, "bob's", Head, "merge").GetAwaiter().GetResult();
			}
			return (document with {
				Entries = [.. document.Entries, new MergeQueueEntry(1, "alice's", "aaa", "squash", "alice@host", DateTimeOffset.UtcNow)],
			}, "enqueue #1 by alice@host");
		});

		Assert.That((await Queue(bob).ReadAsync()).Document.Entries, Has.Count.EqualTo(2),
			"a parentless commit cannot fast-forward an existing ref, so the queue cannot fork");
	}

	[Test]
	public async Task OnlyOneOfTwoClientsTakesTheLock()
	{
		await Queue(alice).EnqueueAsync(142, "A change", Head, "squash");

		Assert.That(await Queue(alice).TryAcquireAsync(142), Is.True);
		Assert.That(await Queue(bob).TryAcquireAsync(142), Is.False);
	}

	[Test]
	public async Task TheSameClientAsksForItsOwnLockAgainAndGetsIt()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(142, "A change", Head, "squash");

		Assert.That(await queue.TryAcquireAsync(142), Is.True);
		Assert.That(await queue.TryAcquireAsync(142), Is.True,
			"a client that crashed and reconnected would otherwise be locked out by itself");
	}

	[Test]
	public async Task ALockNobodyRenewedCanBeTakenOver()
	{
		await Queue(alice).EnqueueAsync(142, "A change", Head, "squash");
		await LeaveLockAgedBy(MergeQueueService.LeaseTime + TimeSpan.FromMinutes(1));

		Assert.That(await Queue(bob).TryAcquireAsync(142), Is.True);
		Assert.That((await Queue(bob).ReadAsync()).Document.Lock!.Holder, Is.EqualTo("bob@host"));
	}

	[Test]
	public async Task ALockStillWithinItsLeaseIsLeftAlone()
	{
		await Queue(alice).EnqueueAsync(142, "A change", Head, "squash");
		await LeaveLockAgedBy(MergeQueueService.LeaseTime - TimeSpan.FromMinutes(1));

		Assert.That(await Queue(bob).TryAcquireAsync(142), Is.False);
		Assert.That((await Queue(bob).ReadAsync()).Document.Lock!.Holder, Is.EqualTo("ghost@host"));
	}

	[Test]
	public async Task ReleasingSomebodyElsesLockDoesNothing()
	{
		await Queue(alice).EnqueueAsync(142, "A change", Head, "squash");
		await Queue(alice).TryAcquireAsync(142);

		await Queue(bob).ReleaseAsync();

		Assert.That((await Queue(bob).ReadAsync()).Document.Lock, Is.Not.Null);
	}

	[Test]
	public async Task AClientRecognisesItsOwnLockAndNobodyElses()
	{
		var mine = Queue(alice);
		await mine.EnqueueAsync(142, "A change", Head, "squash");
		await mine.TryAcquireAsync(142);

		Assert.That(mine.HoldsLock((await mine.ReadAsync()).Document), Is.True);
		Assert.That(Queue(bob).HoldsLock((await mine.ReadAsync()).Document), Is.False);
		Assert.That(Queue(alice).HoldsLock((await mine.ReadAsync()).Document), Is.False,
			"another window of the same person on the same machine is not the same client");
	}

	[Test]
	public async Task AStuckLockCanBeBrokenWithoutWaitingOutTheLease()
	{
		await Queue(alice).EnqueueAsync(142, "A change", Head, "squash");
		await Queue(alice).TryAcquireAsync(142);

		string? broken = await Queue(bob).BreakLockAsync();

		Assert.That(broken, Is.EqualTo("alice@host"));
		Assert.That((await Queue(bob).ReadAsync()).Document.Lock, Is.Null);
		Assert.That(await Queue(bob).TryAcquireAsync(142), Is.True,
			"breaking it has to leave the queue takeable, not merely empty-handed");
	}

	[Test]
	public async Task BreakingALockSaysWhoBrokeItAndWhose()
	{
		await Queue(alice).EnqueueAsync(142, "A change", Head, "squash");
		await Queue(alice).TryAcquireAsync(142);

		await Queue(bob).BreakLockAsync();

		Assert.That(await Git(origin, "log", "--format=%s", MergeQueueService.QueueRef),
			Does.Contain("lock broken by bob@host, was alice@host's for #142"));
	}

	[Test]
	public async Task BreakingNothingIsNotAFailure()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(142, "A change", Head, "squash");

		Assert.That(await queue.BreakLockAsync(), Is.Null);
		Assert.That((await queue.ReadAsync()).Document.Entries, Has.Count.EqualTo(1));
	}

	[Test]
	public async Task RemovingSomethingNobodyQueuedIsNotAFailure()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(142, "A change", Head, "squash");

		await queue.RemoveAsync(999, "merged");

		Assert.That((await queue.ReadAsync()).Document.Entries, Has.Count.EqualTo(1));
		Assert.That(await Git(origin, "rev-list", "--count", MergeQueueService.QueueRef),
			Does.StartWith("1"), "a no-op edit publishes nothing");
	}

	[Test]
	public async Task ManyEntriesGoOutInOneWrite()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(1, "A change", Head, "squash");
		await queue.EnqueueAsync(2, "A change", Head, "squash");
		await queue.EnqueueAsync(3, "A change", Head, "squash");
		string before = (await Git(origin, "rev-list", "--count", MergeQueueService.QueueRef)).Trim();

		await queue.RemoveAsync([1, 3], "could not be merged");

		Assert.That((await queue.ReadAsync()).Document.Entries.Select(e => e.Pr), Is.EqualTo(new[] { 2 }));
		Assert.That((await Git(origin, "rev-list", "--count", MergeQueueService.QueueRef)).Trim(),
			Is.EqualTo((int.Parse(before) + 1).ToString()),
			"two entries out is one state, not two");
	}

	[Test]
	public async Task ClearingSaysHowManyWentAndNamesThem()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(1, "A change", Head, "squash");
		await queue.EnqueueAsync(2, "A change", Head, "squash");

		await queue.RemoveAsync([1, 2], "queue emptied by hand");

		Assert.That(await Git(origin, "log", "--format=%s", MergeQueueService.QueueRef),
			Does.Contain("remove 2 entries (#1, #2): queue emptied by hand"));
		Assert.That((await queue.ReadAsync()).Document.Entries, Is.Empty);
	}

	[Test]
	public async Task RemovingNoneOfThemWritesNothing()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(1, "A change", Head, "squash");

		await queue.RemoveAsync([7, 8], "could not be merged");

		Assert.That(await Git(origin, "rev-list", "--count", MergeQueueService.QueueRef),
			Does.StartWith("1"));
	}

	[Test]
	public async Task AnEntryGitHubWillNotDescribeDoesNotStallTheOnesBehindIt()
	{
		// gh has no repository to answer about in a bare temp clone, so every entry fails the
		// same way a deleted pull request would - which is the point: the turn has to come back
		// with a reason per entry rather than throw the first one out of the whole queue.
		var queue = Queue(alice);
		await queue.EnqueueAsync(9998, "Gone", Head, "merge");
		await queue.EnqueueAsync(9999, "Also gone", Head, "merge");

		var result = await queue.DriveOnceAsync();

		Assert.That(result.Blocked.Select(b => b.Pr), Is.EqualTo(new[] { 9998, 9999 }),
			"every entry has to be reached and reported on, not just the first");
		Assert.That((await queue.ReadAsync()).Document.Entries, Has.Count.EqualTo(2),
			"a queue that could not be read is not a queue to empty");
	}

	[Test]
	public async Task MovingAnEntryReordersTheQueue()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(1, "A change", Head, "squash");
		await queue.EnqueueAsync(2, "A change", Head, "squash");
		await queue.EnqueueAsync(3, "A change", Head, "squash");

		await queue.MoveAsync(3, -2);

		Assert.That((await queue.ReadAsync()).Document.Entries.Select(e => e.Pr),
			Is.EqualTo(new[] { 3, 1, 2 }));
	}

	[Test]
	public async Task MovingSomethingNobodyQueuedIsNotAFailure()
	{
		var queue = Queue(alice);

		await queue.MoveAsync(42, 1);

		Assert.That((await queue.ReadAsync()).Document.Entries, Is.Empty);
	}

	[Test]
	public async Task EveryChangeLeavesALineInTheRefsOwnHistory()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(142, "Fix blame gutter", Head, "squash");
		await queue.TryAcquireAsync(142);
		await queue.RemoveAsync(142, "merged");

		string log = await Git(origin, "log", "--format=%s", MergeQueueService.QueueRef);

		Assert.That(log, Does.Contain("enqueue #142 by alice@host"));
		Assert.That(log, Does.Contain("lock taken by alice@host for #142"));
		Assert.That(log, Does.Contain("remove #142: merged"));
	}

	[Test]
	public async Task WhyAnEntryIsGoneComesFromTheQueuesOwnHistory()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(142, "Fix blame gutter", Head, "squash");
		await queue.RemoveAsync(142, "already merged");

		// Bob's clone made none of those changes and has never read the queue; the answer still
		// has to come, because an entry the drainer workflow took out looks exactly like this.
		Assert.That(await Queue(bob).WhyGoneAsync(142), Is.EqualTo("remove #142: already merged"));
	}

	[Test]
	public async Task AnEntryStillQueuedHasNoDepartureToExplain()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(142, "Fix blame gutter", Head, "squash");
		await queue.TryAcquireAsync(142);

		Assert.That(await queue.WhyGoneAsync(142), Is.Null,
			"being queued and being locked are not things that took it out");
	}

	[Test]
	public async Task ANumberIsNotAnsweredForByOneThatStartsWithIt()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(14, "Shorter", Head, "squash");
		await queue.EnqueueAsync(142, "Longer", Head, "squash");
		await queue.RemoveAsync(142, "already merged");

		Assert.That(await queue.WhyGoneAsync(14), Is.Null, "#142 leaving says nothing about #14");
		Assert.That(await queue.WhyGoneAsync(142), Does.Contain("already merged"));
	}

	[Test]
	public async Task AQueueEmptiedByHandSaysSoForEveryEntryItTookOut()
	{
		var queue = Queue(alice);
		await queue.EnqueueAsync(1, "A change", Head, "squash");
		await queue.EnqueueAsync(2, "Another change", Head, "squash");

		await queue.RemoveAsync([1, 2], "queue emptied by hand");

		Assert.That(await queue.WhyGoneAsync(1), Does.Contain("queue emptied by hand"));
		Assert.That(await queue.WhyGoneAsync(2), Does.Contain("queue emptied by hand"));
	}

	[Test]
	public async Task WhetherToDeleteTheBranchTravelsWithTheEntry()
	{
		await Queue(alice).EnqueueAsync(142, "Fix blame gutter", Head, "squash", deleteBranch: true);

		var entry = (await Queue(bob).ReadAsync()).Document.Find(142);

		Assert.That(entry!.DeleteBranch, Is.True,
			"whoever drains the queue has to merge it the way the enqueuer meant");
	}

	[Test]
	public async Task AnEntryQueuedBeforeTheOptionExistedLeavesTheBranchAlone()
	{
		// What a client that predates the field published: the same document without it. Written
		// straight onto the ref, because nothing here can produce one any more.
		await PublishRawDocument("enqueue #7 by carol@host",
			"""{"version":1,"entries":[{"pr":7,"title":"Older","headSha":"","method":"squash","by":"carol@host","at":"2026-01-01T00:00:00+00:00"}],"lock":null}""");

		var entry = (await Queue(alice).ReadAsync()).Document.Find(7);

		Assert.That(entry, Is.Not.Null, "an older document still has to read");
		Assert.That(entry!.DeleteBranch, Is.False);
	}

	/// <summary>Puts a document on the queue ref as it stands, bypassing the serializer - the
	/// only way to test reading something this build would not write.</summary>
	async Task PublishRawDocument(string subject, string json)
	{
		// The empty tree, by the sha every sha-1 repository gives it: a commit has to point at
		// one, and the queue's own commits point at nothing else either.
		const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
		string sha = (await Git(alice, "commit-tree", EmptyTree, "-m", subject + "\n\n" + json)).Trim();
		await Git(alice, "push", "--quiet", "origin", $"{sha}:{MergeQueueService.QueueRef}");
	}

	/// <summary>Puts a lock of somebody who is no longer around on the queue, stamped far enough
	/// in the past that the lease decides the outcome.</summary>
	async Task LeaveLockAgedBy(TimeSpan age)
		=> await Queue(alice).UpdateAsync(document => (
			document with { Lock = new MergeQueueLock("ghost@host", "ghost", DateTimeOffset.UtcNow - age, 142) },
			"lock taken by ghost@host for #142"));

	MergeQueueService Queue(string clone)
		=> new(new GitService(clone), new GitHubService(clone),
			identity: (clone == alice ? "alice" : "bob") + "@host");

	const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

	async Task<string> Clone(string who)
	{
		string dir = NewDirectory();
		await Git(dir, "clone", "--quiet", origin, dir);
		await Git(dir, "config", "user.name", who);
		await Git(dir, "config", "user.email", who.ToLowerInvariant() + "@example.com");
		return dir;
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
