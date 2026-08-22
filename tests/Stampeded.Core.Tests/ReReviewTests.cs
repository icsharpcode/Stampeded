using NUnit.Framework;

using Stampeded.Core.Review;

namespace Stampeded.Core.Tests;

public class ReReviewTests
{
	[Test]
	public void ViewedCarriesOverOnlyForUntouchedFiles()
	{
		var previousViewed = new Dictionary<string, bool> {
			["a.cs"] = true,   // untouched -> carries
			["b.cs"] = true,   // touched -> invalidated
			["c.cs"] = false,  // never viewed -> nothing to carry
		};
		var touched = new HashSet<string> { "b.cs", "d.cs" };

		var carried = ReReview.CarryOverViewed(previousViewed, touched);

		Assert.That(carried, Is.EquivalentTo(new[] { "a.cs" }));
	}

	[Test]
	public void StoreCapturesSupersededStateOnHeadMove()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N"));
		try
		{
			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "sha-one");
			store.SetViewed("a.cs", true);
			Assert.That(store.Superseded, Is.Null);

			store.Open("repo", 1, "sha-two");
			Assert.That(store.Superseded, Is.Not.Null);
			Assert.That(store.Superseded!.Value.PreviousHead, Is.EqualTo("sha-one"));
			Assert.That(store.Superseded!.Value.PreviousViewed["a.cs"], Is.True);
			Assert.That(store.IsViewed("a.cs"), Is.False, "viewed resets at the new head until carried over");

			store.Open("repo", 1, "sha-two");
			Assert.That(store.Superseded, Is.Null, "same head is not a re-review");
		}
		finally
		{
			TempDirectory.Delete(dir);
		}
	}

	[Test]
	public void ThePreviousHeadOutlivesTheOpenThatDiscoveredTheMove()
	{
		// The baseline has to survive closing the app: a reader who comes back tomorrow still
		// wants the diff against what they read yesterday, not a fresh pass over everything.
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N"));
		try
		{
			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "sha-one", "base-one");
			store.SetViewed("a.cs", true);
			Assert.That(store.PreviousHead, Is.Null, "a first pass has nothing before it");

			store.Open("repo", 1, "sha-two", "base-two");
			Assert.That(store.PreviousHead, Is.EqualTo("sha-one"));
			Assert.That(store.PreviousBase, Is.EqualTo("base-one"));

			var reopened = new ReviewStateStore(dir);
			reopened.Open("repo", 1, "sha-two", "base-two");

			Assert.That(reopened.Superseded, Is.Null, "the move was already handled");
			Assert.That(reopened.PreviousHead, Is.EqualTo("sha-one"), "but the baseline is still there");
			Assert.That(reopened.PreviousBase, Is.EqualTo("base-one"));
		}
		finally
		{
			TempDirectory.Delete(dir);
		}
	}

	[Test]
	public void StateWrittenBeforeTheBaselineExistedStillLoads()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			File.WriteAllText(Path.Combine(dir, "repo_pr1.json"), """
				{
				  "HeadSha": "sha-one",
				  "Viewed": { "a.cs": true },
				  "Drafts": [],
				  "GuideChecks": {}
				}
				""");

			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "sha-one", "base-one");

			Assert.That(store.IsViewed("a.cs"), Is.True, "an older state file keeps its answers");
			Assert.That(store.PreviousHead, Is.Null);
		}
		finally
		{
			TempDirectory.Delete(dir);
		}
	}
}
