using NUnit.Framework;

using Stampeded.Core.GitHub;

namespace Stampeded.Core.Tests;

public class IssueLinkTests
{
	const string Prefix = "https://github.com/icsharpcode/ILSpy/issues/";

	static string Link(string markdown) => IssueLinks.Autolink(markdown, Prefix);

	[Test]
	public void LinksAReference()
	{
		Assert.That(Link("Fixes #3972 for good."),
			Is.EqualTo($"Fixes [#3972]({Prefix}3972) for good."));
	}

	[Test]
	public void LeavesCodeAlone()
	{
		// Preprocessor directives, colours and anything else inside code is not a reference.
		Assert.That(Link("Use `#region Foo` here"), Is.EqualTo("Use `#region Foo` here"));
		Assert.That(Link("```\nif (x) // see #12\n```"), Is.EqualTo("```\nif (x) // see #12\n```"));
		Assert.That(Link("The colour `#404040` is fine"), Is.EqualTo("The colour `#404040` is fine"));
	}

	[Test]
	public void LeavesSomethingThatIsAlreadyALinkAlone()
	{
		Assert.That(Link("[#12](https://example.com/12)"), Is.EqualTo("[#12](https://example.com/12)"));
		Assert.That(Link("see https://github.com/o/r/pull/5#issuecomment-9 for the rest"),
			Is.EqualTo("see https://github.com/o/r/pull/5#issuecomment-9 for the rest"));
	}

	[Test]
	public void LeavesThingsThatMerelyLookLikeOne()
	{
		Assert.That(Link("# Heading"), Is.EqualTo("# Heading"));
		Assert.That(Link("## 3 reasons"), Is.EqualTo("## 3 reasons"));
		Assert.That(Link("issue#12"), Is.EqualTo("issue#12"), "attached to a word, so not a reference");
		Assert.That(Link("#12abc"), Is.EqualTo("#12abc"));
	}

	[Test]
	public void LinksAReferenceThatStartsALine()
	{
		// "#1234" is a reference, not a heading: a heading needs the space, and a reader who
		// opens a line with an issue number means the issue.
		Assert.That(Link("#3972 is the culprit"), Is.EqualTo($"[#3972]({Prefix}3972) is the culprit"));
		Assert.That(Link("Fixed.\n#12 too"), Is.EqualTo($"Fixed.\n[#12]({Prefix}12) too"));
	}

	[Test]
	public void LinksEveryReferenceInATextWithSeveral()
	{
		Assert.That(Link("Closes #1, #22 and #333."),
			Is.EqualTo($"Closes [#1]({Prefix}1), [#22]({Prefix}22) and [#333]({Prefix}333)."));
	}

	[Test]
	public void DoesNothingWithoutARepositoryToPointAt()
	{
		Assert.That(IssueLinks.Autolink("Fixes #3972.", null), Is.EqualTo("Fixes #3972."));
		Assert.That(IssueLinks.Autolink("Fixes #3972.", ""), Is.EqualTo("Fixes #3972."));
	}
}
