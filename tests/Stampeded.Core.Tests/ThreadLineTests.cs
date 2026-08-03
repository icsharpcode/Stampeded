using NUnit.Framework;

using Stampeded.Core.Diff;

namespace Stampeded.Core.Tests;

public class ThreadLineTests
{
	static DiffDocumentModel BuildSample()
		// old: a b c d ; new: a B c d e  -> doc: a, -b, +B, c, d, +e
		=> DiffDocumentBuilder.Build("a\nb\nc\nd\n", "a\nB\nc\nd\ne\n");

	[Test]
	public void InsertsMarkerBelowAnchorAndShiftsMappings()
	{
		var model = BuildSample();
		int docOfNew2 = model.DocLineFromNewLine(2)!.Value; // the +B line

		var withThreads = model.WithThreadLines([new ThreadAnchor(OldSide: false, BlobLine: 2, Key: "n2")]);

		var lines = withThreads.Text.Split('\n');
		Assert.That(lines[docOfNew2], Is.EqualTo("@@thread:n2@@"), "marker sits directly below the anchor line");
		Assert.That(withThreads.Tags[docOfNew2].Kind, Is.EqualTo(DiffLineKind.Comment));
		Assert.That(withThreads.Tags[docOfNew2].NewLine, Is.EqualTo(0));
		// Mappings before the insertion are unchanged, after it shift by one.
		Assert.That(withThreads.DocLineFromNewLine(2), Is.EqualTo(docOfNew2));
		Assert.That(withThreads.DocLineFromNewLine(3), Is.EqualTo(model.DocLineFromNewLine(3) + 1));
		Assert.That(withThreads.DocLineFromOldLine(2), Is.EqualTo(model.DocLineFromOldLine(2)));
	}

	[Test]
	public void OldSideAnchorAndMultipleThreadsOnOneLine()
	{
		var model = BuildSample();
		int docOfOld2 = model.DocLineFromOldLine(2)!.Value; // the -b line

		var withThreads = model.WithThreadLines([
			new ThreadAnchor(true, 2, "o2-first"),
			new ThreadAnchor(true, 2, "o2-second"),
		]);

		var lines = withThreads.Text.Split('\n');
		Assert.That(lines[docOfOld2], Is.EqualTo("@@thread:o2-first@@"));
		Assert.That(lines[docOfOld2 + 1], Is.EqualTo("@@thread:o2-second@@"));
	}

	[Test]
	public void HunkSpansStretchOverInsertedThreads()
	{
		var model = BuildSample();
		var hunk = model.Hunks[0]; // covers -b/+B
		var withThreads = model.WithThreadLines([new ThreadAnchor(false, 2, "k")]);
		// The thread hangs below +B (the hunk's last line): the hunk stretches over it.
		Assert.That(withThreads.Hunks[0].FirstDocLine, Is.EqualTo(hunk.FirstDocLine));
		Assert.That(withThreads.Hunks[0].LastDocLine, Is.EqualTo(hunk.LastDocLine + 1));
	}

	[Test]
	public void UnresolvableAnchorsAreIgnored()
	{
		var model = BuildSample();
		Assert.That(model.WithThreadLines([new ThreadAnchor(false, 999, "x")]), Is.SameAs(model));
	}
}
