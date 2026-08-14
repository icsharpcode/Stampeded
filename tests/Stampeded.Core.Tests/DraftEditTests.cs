using NUnit.Framework;

using Stampeded.Core.Review;

namespace Stampeded.Core.Tests;

public class DraftEditTests
{
	static CommentAnchor Anchor() => CommentAnchor.Create("a.cs", oldSide: false, line: 3,
		fileLines: ["one", "two", "three", "four"]);

	[Test]
	public void RewritesADraftInPlace()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		try
		{
			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "sha");
			var draft = new StoredComment(Guid.NewGuid(), Anchor(), "first wording", DateTimeOffset.Now);
			store.AddDraft(draft);

			store.UpdateDraft(draft.Id, "said better");

			// One draft, the same one: where it hangs and when it was written are unchanged.
			var stored = store.Drafts.Single();
			Assert.That(stored.Body, Is.EqualTo("said better"));
			Assert.That(stored.Id, Is.EqualTo(draft.Id));
			Assert.That(stored.CreatedAt, Is.EqualTo(draft.CreatedAt));
			Assert.That(stored.Anchor, Is.EqualTo(draft.Anchor));
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Test]
	public void SurvivesReopeningTheReview()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		try
		{
			var draft = new StoredComment(Guid.NewGuid(), Anchor(), "first wording", DateTimeOffset.Now);
			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "sha");
			store.AddDraft(draft);
			store.UpdateDraft(draft.Id, "said better");

			var reopened = new ReviewStateStore(dir);
			reopened.Open("repo", 1, "sha");

			Assert.That(reopened.Drafts.Single().Body, Is.EqualTo("said better"));
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	[Test]
	public void IgnoresADraftThatIsNotThere()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		try
		{
			var store = new ReviewStateStore(dir);
			store.Open("repo", 1, "sha");
			store.AddDraft(new StoredComment(Guid.NewGuid(), Anchor(), "kept", DateTimeOffset.Now));

			store.UpdateDraft(Guid.NewGuid(), "from nowhere");

			Assert.That(store.Drafts.Single().Body, Is.EqualTo("kept"));
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}
}
