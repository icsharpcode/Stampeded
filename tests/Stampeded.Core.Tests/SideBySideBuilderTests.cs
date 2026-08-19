using NUnit.Framework;

using Stampeded.Core.Diff;

namespace Stampeded.Core.Tests;

[TestFixture]
public class SideBySideBuilderTests
{
	const string OldText = """
		line a
		line b
		line c
		line d
		""";

	const string NewText = """
		line a
		line b CHANGED
		line c
		inserted line
		line d
		""";

	[Test]
	public void SidesHaveEqualLineCounts()
	{
		var pair = DiffDocumentBuilder.BuildPair(OldText, NewText);
		var left = pair.LeftText.Split('\n');
		var right = pair.RightText.Split('\n');

		Assert.That(left.Length, Is.EqualTo(right.Length));
		Assert.That(pair.LeftTags, Has.Count.EqualTo(left.Length));
		Assert.That(pair.RightTags, Has.Count.EqualTo(right.Length));
	}

	[Test]
	public void NonFillerLinesAreVerbatimBlobLines()
	{
		var pair = DiffDocumentBuilder.BuildPair(OldText, NewText);
		var left = pair.LeftText.Split('\n');
		var right = pair.RightText.Split('\n');
		var oldLines = OldText.ReplaceLineEndings("\n").Split('\n');
		var newLines = NewText.ReplaceLineEndings("\n").Split('\n');

		for (int i = 0; i < left.Length; i++)
		{
			var tag = pair.LeftTags[i];
			if (tag.Kind == DiffLineKind.Filler)
				Assert.That(left[i], Is.Empty);
			else
				Assert.That(left[i], Is.EqualTo(oldLines[tag.OldLine - 1]));
		}
		for (int i = 0; i < right.Length; i++)
		{
			var tag = pair.RightTags[i];
			if (tag.Kind == DiffLineKind.Filler)
				Assert.That(right[i], Is.Empty);
			else
				Assert.That(right[i], Is.EqualTo(newLines[tag.NewLine - 1]));
		}
	}

	[Test]
	public void InsertionProducesLeftFillerOnSameRow()
	{
		var pair = DiffDocumentBuilder.BuildPair(OldText, NewText);

		int insertedRow = pair.RightTags.ToList().FindIndex(t => t.Kind == DiffLineKind.Added && t.NewLine == 4);
		Assert.That(insertedRow, Is.GreaterThanOrEqualTo(0), "inserted line must be an Added row on the right");
		Assert.That(pair.LeftTags[insertedRow].Kind, Is.EqualTo(DiffLineKind.Filler));
	}

	[Test]
	public void ReplacePairSitsOnOneRowWithWordDiffs()
	{
		var pair = DiffDocumentBuilder.BuildPair(OldText, NewText);

		int row = pair.LeftTags.ToList().FindIndex(t => t.Kind == DiffLineKind.Removed && t.OldLine == 2);
		Assert.That(row, Is.GreaterThanOrEqualTo(0));
		Assert.That(pair.RightTags[row].Kind, Is.EqualTo(DiffLineKind.Added));
		Assert.That(pair.RightTags[row].NewLine, Is.EqualTo(2));
		Assert.That(pair.RightTags[row].WordDiffs, Is.Not.Null.And.Not.Empty);
	}

	[Test]
	public void IdenticalTextsProduceNoFillersOrChanges()
	{
		var pair = DiffDocumentBuilder.BuildPair(OldText, OldText);
		Assert.That(pair.LeftTags.All(t => t.Kind == DiffLineKind.Context), Is.True);
		Assert.That(pair.RightTags.All(t => t.Kind == DiffLineKind.Context), Is.True);
	}

	[Test]
	public void ThreadRowIsReservedOnBothSides()
	{
		var pair = DiffDocumentBuilder.BuildPair(OldText, NewText);
		int before = pair.LeftText.Split('\n').Length;
		int row = pair.DocLineFor(oldSide: false, blobLine: 2)!.Value;
		var spliced = pair.WithThreadLines([new ThreadAnchor(OldSide: false, BlobLine: 2, Key: "n2")]);
		var left = spliced.LeftText.Split('\n');
		var right = spliced.RightText.Split('\n');
		Assert.That(left, Has.Length.EqualTo(before + 1));
		Assert.That(right, Has.Length.EqualTo(left.Length), "the panes are kept in step by row count");
		Assert.That(right[row], Is.EqualTo("@@thread:n2@@"));
		Assert.That(left[row], Is.EqualTo("@@thread:n2@@"), "the other side reserves the row too");
		Assert.That(spliced.LeftTags[row].Kind, Is.EqualTo(DiffLineKind.Comment));
		Assert.That(spliced.RightTags[row].Kind, Is.EqualTo(DiffLineKind.Comment));
	}

	[Test]
	public void ThreadRowCarriesNoBlobLine()
	{
		var pair = DiffDocumentBuilder.BuildPair(OldText, NewText)
			.WithThreadLines([new ThreadAnchor(OldSide: true, BlobLine: 1, Key: "o1")]);
		// The row belongs to neither blob, so the side text a parser sees is unchanged.
		Assert.That(pair.GetSideText(oldSide: true).Text, Is.EqualTo(OldText));
		Assert.That(pair.GetSideText(oldSide: false).Text, Is.EqualTo(NewText));
	}

	[Test]
	public void OutdatedThreadGoesAboveTheFirstRow()
	{
		var pair = DiffDocumentBuilder.BuildPair(OldText, NewText)
			.WithThreadLines([new ThreadAnchor(OldSide: false, BlobLine: 0, Key: "od0")]);
		Assert.That(pair.LeftText.Split('\n')[0], Is.EqualTo("@@thread:od0@@"));
		Assert.That(pair.RightText.Split('\n')[0], Is.EqualTo("@@thread:od0@@"));
	}

	[Test]
	public void AnchorOnALineThatSideHasNotIsDropped()
	{
		var pair = DiffDocumentBuilder.BuildPair(OldText, NewText);
		var spliced = pair.WithThreadLines([new ThreadAnchor(OldSide: true, BlobLine: 99, Key: "o99")]);
		Assert.That(spliced, Is.SameAs(pair));
	}
}
