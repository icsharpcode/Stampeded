using NUnit.Framework;

using Stampeded.Core.Git;

namespace Stampeded.Core.Tests;

[TestFixture]
public class GitLogParserTests
{
	[Test]
	public void ParsesTabSeparatedCommits()
	{
		const string log = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\taaaaaaaaa\tAlice\t2026-08-01\tFix the thing\n"
			+ "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\tbbbbbbbbb\tBob B.\t2026-07-30\tSubject\twith\ttabs\n";

		var commits = GitLogParser.Parse(log);

		Assert.That(commits, Has.Count.EqualTo(2));
		Assert.That(commits[0].ShortSha, Is.EqualTo("aaaaaaaaa"));
		Assert.That(commits[0].Author, Is.EqualTo("Alice"));
		Assert.That(commits[0].Date, Is.EqualTo("2026-08-01"));
		Assert.That(commits[0].Subject, Is.EqualTo("Fix the thing"));
		// Tabs inside the subject must survive: only the first four separators split.
		Assert.That(commits[1].Subject, Is.EqualTo("Subject\twith\ttabs"));
	}

	[Test]
	public void EmptyLogYieldsNoCommits()
	{
		Assert.That(GitLogParser.Parse(""), Is.Empty);
	}

	[Test]
	public void ParsesNameStatusIncludingRenames()
	{
		const string output = "M\tsrc/A.cs\nA\tsrc/New.cs\nD\told/Gone.cs\nR100\tfrom/Old.cs\tto/New.cs\n";

		var entries = GitLogParser.ParseNameStatus(output);

		Assert.That(entries, Has.Count.EqualTo(4));
		Assert.That(entries[0], Is.EqualTo(('M', "src/A.cs")));
		Assert.That(entries[1], Is.EqualTo(('A', "src/New.cs")));
		Assert.That(entries[2], Is.EqualTo(('D', "old/Gone.cs")));
		// Renames report old and new path; the new path is what a review opens.
		Assert.That(entries[3], Is.EqualTo(('R', "to/New.cs")));
	}
}
