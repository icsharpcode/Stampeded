using NUnit.Framework;

using Stampeded.Core.Git;

namespace Stampeded.Core.Tests;

[TestFixture]
public class GitLogParserTests
{
	[Test]
	public void ParsesTabSeparatedCommits()
	{
		const string log = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\taaaaaaaaa\tAlice\t2026-08-01\tpppppppp\tFix the thing\n\0"
			+ "\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\tbbbbbbbbb\tBob B.\t2026-07-30\tpppppppp qqqqqqqq\tSubject\twith\ttabs\n\0\n";

		var commits = GitLogParser.Parse(log);

		Assert.That(commits, Has.Count.EqualTo(2));
		Assert.That(commits[0].ShortSha, Is.EqualTo("aaaaaaaaa"));
		Assert.That(commits[0].Author, Is.EqualTo("Alice"));
		Assert.That(commits[0].Date, Is.EqualTo("2026-08-01"));
		Assert.That(commits[0].Subject, Is.EqualTo("Fix the thing"));
		Assert.That(commits[0].Body, Is.Empty);
		// Tabs inside the subject must survive: only the separators before it split.
		Assert.That(commits[1].Subject, Is.EqualTo("Subject\twith\ttabs"));
		// The parents come with the commit; a merge lists more than one, mainline first.
		Assert.That(commits[0].FirstParent, Is.EqualTo("pppppppp"));
		Assert.That(commits[1].FirstParent, Is.EqualTo("pppppppp"));
	}

	[Test]
	public void KeepsTheBodyWholeIncludingItsBlankLines()
	{
		const string log = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\taaaaaaaaa\tAlice\t2026-08-01\tpppppppp\tFix the thing\n"
			+ "Why it had to change.\n\nWhat was rejected.\n\n\0"
			+ "\nbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\tbbbbbbbbb\tBob\t2026-07-30\tpppppppp\tNo body here\n\0\n";

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
	public void ParsesShortStatPerCommit()
	{
		const string output =
			"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n\n 2 files changed, 12 insertions(+), 4 deletions(-)\n"
			+ "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n\n 1 file changed, 3 insertions(+)\n"
			+ "cccccccccccccccccccccccccccccccccccccccc\n\n 1 file changed, 7 deletions(-)\n"
			// A commit that changed nothing (an empty commit, or a merge) prints no summary.
			+ "dddddddddddddddddddddddddddddddddddddddd\n";

		var stats = GitLogParser.ParseShortStat(output);

		Assert.That(stats["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"], Is.EqualTo((12, 4)));
		Assert.That(stats["bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"], Is.EqualTo((3, 0)));
		Assert.That(stats["cccccccccccccccccccccccccccccccccccccccc"], Is.EqualTo((0, 7)));
		Assert.That(stats.ContainsKey("dddddddddddddddddddddddddddddddddddddddd"), Is.False);
	}

	[Test]
	public void CountsHowManyCommitsTouchedEachPath()
	{
		const string output = "src/A.cs\nsrc/B.cs\n\nsrc/A.cs\n\nsrc/A.cs\nsrc/C.cs\n";

		var churn = GitLogParser.CountPathTouches(output);

		Assert.That(churn["src/A.cs"], Is.EqualTo(3));
		Assert.That(churn["src/B.cs"], Is.EqualTo(1));
		Assert.That(churn["src/C.cs"], Is.EqualTo(1));
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
