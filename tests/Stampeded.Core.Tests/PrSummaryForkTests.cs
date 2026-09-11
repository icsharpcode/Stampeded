using NUnit.Framework;

using Stampeded.Core.GitHub;

namespace Stampeded.Core.Tests;

/// <summary>
/// A pull request names its head branch as the repository it lives in names it, so a fork's
/// "master" arrives spelled exactly like the master of the repository being read. Only the
/// head repository's owner tells the two apart.
/// </summary>
public class PrSummaryForkTests
{
	static PrSummary Pr(string? headOwner, string? originOwner) => new(
		1, "t", null, "master", "master", false, DateTimeOffset.UtcNow,
		HeadRepositoryOwner: headOwner is null ? null : new PrRepoOwner(headOwner)) {
		OriginOwner = originOwner,
	};

	[Test]
	public void HeadInAnotherAccountIsAFork()
		=> Assert.That(Pr("contributor", "upstream").HeadIsFork, Is.True);

	[Test]
	public void HeadInOriginIsNot()
		=> Assert.That(Pr("Upstream", "upstream").HeadIsFork, Is.False,
			"GitHub logins differ only in case, so the comparison cannot");

	[Test]
	public void WithoutAnOwnerToCompareAgainstNothingIsAFork()
	{
		Assert.That(Pr("contributor", null).HeadIsFork, Is.False, "origin is not a GitHub remote");
		Assert.That(Pr(null, "upstream").HeadIsFork, Is.False, "the head repository is gone");
	}
}
