using NUnit.Framework;

using Stampeded.Core.Git;

namespace Stampeded.Core.Tests;

[TestFixture]
public class GitLogParserTests
{
	[Test]
	public void ParsesTabSeparatedCommits()
	{
		const string log = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\taaaaaaaaa\tAlice\t2026-08-01\tFix the thing\n\0"
			+ "\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\tbbbbbbbbb\tBob B.\t2026-07-30\tSubject\twith\ttabs\n\0\n";

		var commits = GitLogParser.Parse(log);

		Assert.That(commits, Has.Count.EqualTo(2));
		Assert.That(commits[0].ShortSha, Is.EqualTo("aaaaaaaaa"));
		Assert.That(commits[0].Author, Is.EqualTo("Alice"));
		Assert.That(commits[0].Date, Is.EqualTo("2026-08-01"));
		Assert.That(commits[0].Subject, Is.EqualTo("Fix the thing"));
		Assert.That(commits[0].Body, Is.Empty);
		// Tabs inside the subject must survive: only the first four separators split.
		Assert.That(commits[1].Subject, Is.EqualTo("Subject\twith\ttabs"));
	}

	[Test]
	public void KeepsTheBodyWholeIncludingItsBlankLines()
	{
		const string log = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\taaaaaaaaa\tAlice\t2026-08-01\tFix the thing\n"
			+ "Why it had to change.\n\nWhat was rejected.\n\n\0"
			+ "\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\tbbbbbbbbb\tBob\t2026-07-30\tNo body here\n\0\n";

		var commits = GitLogParser.Parse(log);

		Assert.That(commits, Has.Count.EqualTo(2));
		Assert.That(commits[0].Body, Is.EqualTo("Why it had to change.\n\nWhat was rejected."));
		Assert.That(commits[0].Message, Is.EqualTo("Fix the thing\n\nWhy it had to change.\n\nWhat was rejected."));
		Assert.That(commits[1].Body, Is.Empty);
		Assert.That(commits[1].Message, Is.EqualTo("No body here"));
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

public class GitBranchParserTests
{
	[Test]
	public void ParsesForEachRefBranchLines()
	{
		string output = "master\tabc123\t2026-08-01\tFix things\nfeature/x\tdef456\t2026-07-30\tSubject\twith tab\n\n";
		var branches = Stampeded.Core.Git.GitLogParser.ParseBranches(output);
		Assert.That(branches, Has.Count.EqualTo(2));
		Assert.That(branches[0], Is.EqualTo(new Stampeded.Core.Git.BranchInfo("master", "abc123", "2026-08-01", "Fix things")));
		Assert.That(branches[1].Subject, Is.EqualTo("Subject\twith tab"));
	}
}
