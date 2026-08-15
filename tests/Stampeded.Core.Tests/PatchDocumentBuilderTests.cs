using NUnit.Framework;

using Stampeded.Core.Diff;

namespace Stampeded.Core.Tests;

public class PatchDocumentBuilderTests
{
	// `git show` output: a commit message whose body is indented, then one hunk. The
	// indented prose is the trap - read as patch lines it would be context and a removal.
	const string ShowPatch = """
		commit deadbeef
		Author: A <a@example.com>

		    Subject line

		    - a bullet in the body
		     indented prose

		diff --git a/f.cs b/f.cs
		index 111..222 100644
		--- a/f.cs
		+++ b/f.cs
		@@ -10,4 +10,5 @@ class C
		 	int keep;
		-	int gone;
		+	int fresh;
		+	int alsoFresh;
		 	int tail;
		""";

	static (DiffLineKind Kind, int Old, int New) TagOf(DiffDocumentModel model, string lineText)
	{
		var lines = model.Text.Split('\n');
		int i = Array.IndexOf(lines, lineText);
		Assert.That(i, Is.GreaterThanOrEqualTo(0), $"the patch has no line [{lineText}]");
		var tag = model.Tags[i];
		return (tag.Kind, tag.OldLine, tag.NewLine);
	}

	[Test]
	public void TagsHunkLinesAndNumbersThemFromTheHeader()
	{
		var model = PatchDocumentBuilder.Build(ShowPatch);

		Assert.Multiple(() => {
			Assert.That(TagOf(model, " \tint keep;"), Is.EqualTo((DiffLineKind.Context, 10, 10)));
			Assert.That(TagOf(model, "-\tint gone;"), Is.EqualTo((DiffLineKind.Removed, 11, 0)));
			Assert.That(TagOf(model, "+\tint fresh;"), Is.EqualTo((DiffLineKind.Added, 0, 11)));
			Assert.That(TagOf(model, "+\tint alsoFresh;"), Is.EqualTo((DiffLineKind.Added, 0, 12)));
			Assert.That(TagOf(model, " \tint tail;"), Is.EqualTo((DiffLineKind.Context, 12, 13)));
		});
	}

	[Test]
	public void LeavesTheCommitMessageAndHeadersAsContext()
	{
		var model = PatchDocumentBuilder.Build(ShowPatch);

		Assert.Multiple(() => {
			Assert.That(TagOf(model, "    - a bullet in the body").Kind, Is.EqualTo(DiffLineKind.Context));
			Assert.That(TagOf(model, "     indented prose"), Is.EqualTo((DiffLineKind.Context, 0, 0)));
			Assert.That(TagOf(model, "--- a/f.cs").Kind, Is.EqualTo(DiffLineKind.Context));
			Assert.That(TagOf(model, "+++ b/f.cs").Kind, Is.EqualTo(DiffLineKind.Context));
		});
	}

	[Test]
	public void MarksEachChangedRunAsAHunk()
	{
		var model = PatchDocumentBuilder.Build(ShowPatch);

		var lines = model.Text.Split('\n');
		Assert.That(model.Hunks, Has.Count.EqualTo(1));
		Assert.That(lines[model.Hunks[0].FirstDocLine - 1], Is.EqualTo("-\tint gone;"));
		Assert.That(lines[model.Hunks[0].LastDocLine - 1], Is.EqualTo("+\tint alsoFresh;"));
	}

	[Test]
	public void KeepsTheSecondFileSeparateFromTheFirst()
	{
		string patch = ShowPatch + "\n" + """
			diff --git a/g.cs b/g.cs
			--- a/g.cs
			+++ b/g.cs
			@@ -1 +1 @@
			-old
			+new
			""";
		var model = PatchDocumentBuilder.Build(patch);

		Assert.Multiple(() => {
			Assert.That(TagOf(model, "-old"), Is.EqualTo((DiffLineKind.Removed, 1, 0)));
			Assert.That(TagOf(model, "+new"), Is.EqualTo((DiffLineKind.Added, 0, 1)));
			Assert.That(model.Hunks, Has.Count.EqualTo(2));
		});
	}
}
