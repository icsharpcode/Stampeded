using NUnit.Framework;

using Stampeded.Core.Git;

namespace Stampeded.Core.Tests;

public class BranchSyncTests
{
	[Test]
	public void ClassifiesEachCombinationOfAheadAndBehind()
	{
		Assert.Multiple(() => {
			Assert.That(BranchSync.From(0, 0).State, Is.EqualTo(BranchSyncState.InSync));
			Assert.That(BranchSync.From(3, 0).State, Is.EqualTo(BranchSyncState.Ahead));
			Assert.That(BranchSync.From(0, 2).State, Is.EqualTo(BranchSyncState.Behind));
			Assert.That(BranchSync.From(3, 2).State, Is.EqualTo(BranchSyncState.Diverged));
		});
	}

	[Test]
	public void ReportsTheCountsThatDistinguishTheStates()
	{
		Assert.Multiple(() => {
			Assert.That(BranchSync.From(0, 0).Display, Is.EqualTo("in sync"));
			Assert.That(BranchSync.From(3, 0).Display, Is.EqualTo("3 ahead"));
			Assert.That(BranchSync.From(0, 2).Display, Is.EqualTo("2 behind"));
			Assert.That(BranchSync.From(3, 2).Display, Is.EqualTo("3 ahead, 2 behind"));
			Assert.That(BranchSync.Unfetched.Display, Is.EqualTo("differs"));
		});
	}
}
