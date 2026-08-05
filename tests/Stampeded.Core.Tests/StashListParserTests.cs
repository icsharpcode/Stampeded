using NUnit.Framework;

using Stampeded.Core.Git;

namespace Stampeded.Core.Tests;

public class StashListParserTests
{
	/// <summary>`git stash list` is requested in the same tab-separated shape as the
	/// branch listing, so it goes through the same parser.</summary>
	[Test]
	public void ParsesStashListInBranchFormat()
	{
		string output = string.Join('\n', [
			"stash@{0}\tb05f61c3107a77bf3750d9fd6da714cb5e172ff3\t2026-07-14\tOn master: publish artifacts",
			"stash@{1}\t8fff7f11b9b8ea0c571c2b9dc5b4f47660833dcb\t2025-06-05\tWIP on master: 82e461be8 Change return type",
		]);

		var stashes = GitLogParser.ParseBranches(output);

		Assert.That(stashes, Has.Count.EqualTo(2));
		Assert.That(stashes[0].Name, Is.EqualTo("stash@{0}"));
		Assert.That(stashes[0].Sha, Is.EqualTo("b05f61c3107a77bf3750d9fd6da714cb5e172ff3"));
		Assert.That(stashes[0].Date, Is.EqualTo("2026-07-14"));
		Assert.That(stashes[0].Subject, Is.EqualTo("On master: publish artifacts"));
		Assert.That(stashes[1].Name, Is.EqualTo("stash@{1}"));
	}

	[Test]
	public void KeepsTabsInsideTheSubject()
	{
		var stashes = GitLogParser.ParseBranches("stash@{0}\tabc\t2026-01-01\tOn master:\tmessage\twith tabs");

		Assert.That(stashes[0].Subject, Is.EqualTo("On master:\tmessage\twith tabs"));
	}
}
