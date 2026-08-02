using NUnit.Framework;

using Stampeded.Core.Git;

namespace Stampeded.Core.Tests;

[TestFixture]
public class GitBlameParserTests
{
	const string Porcelain = """
		aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 1 1 2
		author Alice Author
		author-mail <alice@example.com>
		author-time 1700000000
		author-tz +0100
		committer Alice Author
		committer-mail <alice@example.com>
		committer-time 1700000000
		committer-tz +0100
		summary First commit
		filename file.cs
			line one
		aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 2 2
			line two
		bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb 1 3 1
		author Bob Builder
		author-mail <bob@example.com>
		author-time 1750000000
		author-tz +0000
		committer Bob Builder
		committer-mail <bob@example.com>
		committer-time 1750000000
		committer-tz +0000
		summary Second commit
		previous aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa file.cs
		filename file.cs
			line three
		""";

	[Test]
	public void ParsesLinesWithCommitInfo()
	{
		var lines = GitBlameParser.Parse(Porcelain);

		Assert.That(lines, Has.Count.EqualTo(3));

		Assert.That(lines[0].FinalLine, Is.EqualTo(1));
		Assert.That(lines[0].Sha, Does.StartWith("aaaaaaaa"));
		Assert.That(lines[0].Author, Is.EqualTo("Alice Author"));
		Assert.That(lines[0].Summary, Is.EqualTo("First commit"));
		Assert.That(lines[0].AuthorTime, Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(1700000000)));

		// Repeated commit occurrences carry no headers; info must come from the cache.
		Assert.That(lines[1].FinalLine, Is.EqualTo(2));
		Assert.That(lines[1].Author, Is.EqualTo("Alice Author"));

		Assert.That(lines[2].FinalLine, Is.EqualTo(3));
		Assert.That(lines[2].Author, Is.EqualTo("Bob Builder"));
		Assert.That(lines[2].Summary, Is.EqualTo("Second commit"));
	}

	[Test]
	public void EmptyInputYieldsNoLines()
	{
		Assert.That(GitBlameParser.Parse(""), Is.Empty);
	}
}
