using NUnit.Framework;

using Stampeded.Core.Review;

namespace Stampeded.Core.Tests;

/// <summary>
/// Where a draft belongs when the review is being read one commit at a time.
///
/// A scope narrows what is on screen; it does not make a second review. Viewed flags are the
/// scope's - having read a file in one commit says nothing about the next commit's change to
/// it - but a draft is a remark about the pull request, posted against a path and a line of
/// it, and there is only one of those to post to.
/// </summary>
public class DraftScopeTests
{
	static CommentAnchor Anchor() => CommentAnchor.Create("a.cs", oldSide: false, line: 3,
		fileLines: ["one", "two", "three", "four"]);

	static StoredComment Draft(string body) => new(Guid.NewGuid(), Anchor(), body, DateTimeOffset.Now);

	static string TempDir() => Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);

	[Test]
	public void ADraftWrittenWhileReadingOneCommitIsStillThereAfterwards()
	{
		string dir = TempDir();
		try
		{
			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "head");
			store.AddDraft(Draft("about the whole change"));

			store.OpenCommitScope("repo", "abcdef1234");
			store.AddDraft(Draft("about this commit"));

			Assert.That(store.Drafts.Select(d => d.Body),
				Is.EquivalentTo(new[] { "about the whole change", "about this commit" }),
				"a draft written in a commit scope is a draft of the review it narrows");

			// Leaving the scope is what submitting does before it posts, so anything the scope
			// kept to itself would never be submitted at all.
			store.Open("repo", 1, "head");

			Assert.That(store.Drafts.Select(d => d.Body),
				Is.EquivalentTo(new[] { "about the whole change", "about this commit" }));
		}
		finally
		{
			TempDirectory.Delete(dir);
		}
	}

	[Test]
	public void ADraftWrittenWhileReadingTheUncommittedWorkIsTheReviewsToo()
	{
		string dir = TempDir();
		try
		{
			var store = new ReviewStateStore(dir);
			store.OpenLocal("repo", "main..work", "head");
			store.OpenWorkingTreeScope("repo", "abcdef1234");
			store.AddDraft(Draft("about what is not committed yet"));

			store.OpenLocal("repo", "main..work", "head");

			Assert.That(store.Drafts.Single().Body, Is.EqualTo("about what is not committed yet"));
		}
		finally
		{
			TempDirectory.Delete(dir);
		}
	}

	[Test]
	public void ADraftSurvivesSteppingFromOneCommitToTheNext()
	{
		string dir = TempDir();
		try
		{
			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "head");
			store.OpenCommitScope("repo", "aaaaaaaaa1");
			store.AddDraft(Draft("about the first commit"));

			// Stepping through the series re-keys the scope for every commit; the review's
			// drafts are not one of the things that changes with the step.
			store.OpenCommitScope("repo", "bbbbbbbbb2");

			Assert.That(store.Drafts.Single().Body, Is.EqualTo("about the first commit"));
			store.AddDraft(Draft("about the second commit"));
			Assert.That(store.Drafts, Has.Count.EqualTo(2));
		}
		finally
		{
			TempDirectory.Delete(dir);
		}
	}

	[Test]
	public void ViewedFlagsStayTheScopesOwn()
	{
		string dir = TempDir();
		try
		{
			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "head");
			store.SetViewed("a.cs", true);

			store.OpenCommitScope("repo", "abcdef1234");

			Assert.That(store.IsViewed("a.cs"), Is.False,
				"having read a file in the whole change says nothing about one commit's change to it");
			store.SetViewed("a.cs", true);

			store.Open("repo", 1, "head");
			Assert.That(store.IsViewed("a.cs"), Is.True, "and the whole change keeps its own answer");
		}
		finally
		{
			TempDirectory.Delete(dir);
		}
	}

	[Test]
	public void EditingAndRemovingReachTheReviewsDraftsFromInsideAScope()
	{
		string dir = TempDir();
		try
		{
			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "head");
			var kept = Draft("kept");
			var edited = Draft("first wording");
			store.AddDraft(kept);
			store.AddDraft(edited);

			store.OpenCommitScope("repo", "abcdef1234");
			store.UpdateDraft(edited.Id, "said better");
			store.RemoveDraft(kept.Id);

			store.Open("repo", 1, "head");

			Assert.That(store.Drafts.Single().Body, Is.EqualTo("said better"));
		}
		finally
		{
			TempDirectory.Delete(dir);
		}
	}
}
