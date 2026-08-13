using NUnit.Framework;

using Stampeded.Core.Diff;

namespace Stampeded.Core.Tests;

[TestFixture]
public class DiffDocumentBuilderTests
{
	const string OldText = """
		using System;

		class Calculator
		{
			static int Add(int a, int b) => a + b;
			static int Sub(int a, int b) => a - b;
			static int Mul(int a, int b) => a * b;
		}
		""";

	const string NewText = """
		using System;

		// entry point
		static class Calculator
		{
			static int Add(int a, int b) => a + b;
			static int Mul(int a, int b) => a * b;
			static int Div(int a, int b) => a / b;
		}
		""";

	static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

	[Test]
	public void EveryDocumentLineIsAVerbatimBlobLine()
	{
		var model = DiffDocumentBuilder.Build(OldText, NewText);
		var docLines = model.Text.Split('\n');
		var oldLines = Lines(OldText);
		var newLines = Lines(NewText);

		Assert.That(model.Tags, Has.Count.EqualTo(docLines.Length));
		for (int i = 0; i < docLines.Length; i++)
		{
			var tag = model.Tags[i];
			switch (tag.Kind)
			{
				case DiffLineKind.Removed:
					Assert.That(tag.OldLine, Is.GreaterThan(0));
					Assert.That(tag.NewLine, Is.EqualTo(0));
					Assert.That(docLines[i], Is.EqualTo(oldLines[tag.OldLine - 1]),
						$"doc line {i + 1} must be verbatim old line {tag.OldLine}");
					break;
				case DiffLineKind.Added:
					Assert.That(tag.NewLine, Is.GreaterThan(0));
					Assert.That(tag.OldLine, Is.EqualTo(0));
					Assert.That(docLines[i], Is.EqualTo(newLines[tag.NewLine - 1]),
						$"doc line {i + 1} must be verbatim new line {tag.NewLine}");
					break;
				case DiffLineKind.Context:
					Assert.That(tag.OldLine, Is.GreaterThan(0));
					Assert.That(tag.NewLine, Is.GreaterThan(0));
					Assert.That(docLines[i], Is.EqualTo(newLines[tag.NewLine - 1]));
					Assert.That(docLines[i], Is.EqualTo(oldLines[tag.OldLine - 1]));
					break;
				default:
					Assert.Fail($"unexpected {tag.Kind} in unified document");
					break;
			}
		}
	}

	[Test]
	public void EveryBlobLineAppearsExactlyOnce()
	{
		var model = DiffDocumentBuilder.Build(OldText, NewText);
		int oldCount = Lines(OldText).Length;
		int newCount = Lines(NewText).Length;

		var oldSeen = model.Tags.Where(t => t.OldLine > 0).Select(t => t.OldLine).ToList();
		var newSeen = model.Tags.Where(t => t.NewLine > 0).Select(t => t.NewLine).ToList();

		Assert.That(oldSeen, Is.EqualTo(Enumerable.Range(1, oldCount)), "old lines in order, complete");
		Assert.That(newSeen, Is.EqualTo(Enumerable.Range(1, newCount)), "new lines in order, complete");
	}

	[Test]
	public void PositionMappingRoundTrips()
	{
		var model = DiffDocumentBuilder.Build(OldText, NewText);

		for (int doc = 1; doc <= model.Tags.Count; doc++)
		{
			var tag = model.Tags[doc - 1];
			if (tag.NewLine > 0)
				Assert.That(model.DocLineFromNewLine(tag.NewLine), Is.EqualTo(doc));
			if (tag.OldLine > 0)
				Assert.That(model.DocLineFromOldLine(tag.OldLine), Is.EqualTo(doc));
		}
	}

	[Test]
	public void HunksAreMaximalChangedRuns()
	{
		var model = DiffDocumentBuilder.Build(OldText, NewText);

		Assert.That(model.Hunks, Is.Not.Empty);
		foreach (var hunk in model.Hunks)
		{
			for (int line = hunk.FirstDocLine; line <= hunk.LastDocLine; line++)
				Assert.That(model.Tags[line - 1].Kind, Is.Not.EqualTo(DiffLineKind.Context));
			if (hunk.FirstDocLine > 1)
				Assert.That(model.Tags[hunk.FirstDocLine - 2].Kind, Is.EqualTo(DiffLineKind.Context));
			if (hunk.LastDocLine < model.Tags.Count)
				Assert.That(model.Tags[hunk.LastDocLine].Kind, Is.EqualTo(DiffLineKind.Context));
		}
		// The sample has two separated changed regions plus the trailing addition.
		Assert.That(model.Hunks.Count, Is.GreaterThanOrEqualTo(2));
	}

	[Test]
	public void ReplacePairsCarryWordDiffs()
	{
		var model = DiffDocumentBuilder.Build("int x = 1;", "int y = 1;");

		var removed = model.Tags.Single(t => t.Kind == DiffLineKind.Removed);
		var added = model.Tags.Single(t => t.Kind == DiffLineKind.Added);
		Assert.That(removed.WordDiffs, Is.Not.Null.And.Not.Empty);
		Assert.That(added.WordDiffs, Is.Not.Null.And.Not.Empty);
		// The changed identifier is at offset 4, length 1, on both sides.
		Assert.That(added.WordDiffs![0].Start, Is.EqualTo(4));
	}

	[Test]
	public void RenamedIdentifierIsOneSpanRatherThanTheLettersItShares()
	{
		var model = DiffDocumentBuilder.Build("int oldName = 1;", "int newName = 1;");

		var added = model.Tags.Single(t => t.Kind == DiffLineKind.Added);
		// Character comparison keeps the shared "Name" suffix out of the highlight and lights
		// only "old"/"new"; the identifier was replaced as a whole and highlights as a whole.
		Assert.That(added.WordDiffs, Has.Count.EqualTo(1));
		Assert.That(added.WordDiffs![0], Is.EqualTo(new IntraLineSpan(4, "newName".Length)));
	}

	[Test]
	public void UnchangedWordsOnAChangedLineStayOutOfTheHighlight()
	{
		var model = DiffDocumentBuilder.Build("var total = a + b;", "var total = a - b;");

		var added = model.Tags.Single(t => t.Kind == DiffLineKind.Added);
		Assert.That(added.WordDiffs, Has.Count.EqualTo(1));
		Assert.That(added.WordDiffs![0], Is.EqualTo(new IntraLineSpan("var total = a ".Length, 1)));
	}

	[Test]
	public void IdenticalTextsProduceNoHunks()
	{
		var model = DiffDocumentBuilder.Build(OldText, OldText);
		Assert.That(model.Hunks, Is.Empty);
		Assert.That(model.Tags.All(t => t.Kind == DiffLineKind.Context), Is.True);
	}
}
