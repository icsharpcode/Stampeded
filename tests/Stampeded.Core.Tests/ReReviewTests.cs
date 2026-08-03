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
			Directory.Delete(dir, recursive: true);
		}
	}
}
