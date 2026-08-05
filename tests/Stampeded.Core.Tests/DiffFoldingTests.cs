using NUnit.Framework;

using Stampeded.Core.Diff;

namespace Stampeded.Core.Tests;

public class DiffFoldingTests
{
	static IReadOnlyList<DiffLineTag> Tags(string shape)
	{
		// '.' = context, '+' = added, '-' = removed
		var tags = new List<DiffLineTag>();
		int old = 0, @new = 0;
		foreach (char c in shape)
		{
			tags.Add(c switch {
				'+' => new DiffLineTag(DiffLineKind.Added, 0, ++@new, null),
				'-' => new DiffLineTag(DiffLineKind.Removed, ++old, 0, null),
				_ => new DiffLineTag(DiffLineKind.Context, ++old, ++@new, null),
			});
		}
		return tags;
	}

	[Test]
	public void FoldsNothingWhenTheDocumentHasNoChanges()
	{
		Assert.That(DiffFolding.UnchangedRuns(Tags(new string('.', 50)), hasChanges: false), Is.Empty);
	}

	[Test]
	public void KeepsContextLinesVisibleBesideAHunk()
	{
		// 20 unchanged, one added line, 20 unchanged.
		var ranges = DiffFolding.UnchangedRuns(Tags(new string('.', 20) + "+" + new string('.', 20)), hasChanges: true);

		Assert.That(ranges, Has.Count.EqualTo(2));
		// Leading run: folds from line 1 (document edge) to 3 lines before the hunk.
		Assert.That(ranges[0].StartLine, Is.EqualTo(1));
		Assert.That(ranges[0].EndLine, Is.EqualTo(20 - DiffFolding.Context));
		// Trailing run: starts 3 lines after the hunk and folds to the last line.
		Assert.That(ranges[1].StartLine, Is.EqualTo(22 + DiffFolding.Context));
		Assert.That(ranges[1].EndLine, Is.EqualTo(41));
		Assert.That(ranges, Has.All.Matches<FoldRange>(r => r.DefaultClosed && !r.FromHeaderEnd));
	}

	[Test]
	public void SkipsRunsTooShortToBeWorthHiding()
	{
		// Six unchanged lines between two hunks: three of context on each side leaves
		// nothing to hide.
		var ranges = DiffFolding.UnchangedRuns(Tags("+" + new string('.', 6) + "+"), hasChanges: true);

		Assert.That(ranges, Is.Empty);
	}

	[Test]
	public void NamesAFoldByHowManyLinesItHides()
	{
		var ranges = DiffFolding.UnchangedRuns(Tags("+" + new string('.', 30) + "+"), hasChanges: true);

		Assert.That(ranges, Has.Count.EqualTo(1));
		Assert.That(ranges[0].EndLine - ranges[0].StartLine + 1, Is.EqualTo(30 - 2 * DiffFolding.Context));
		Assert.That(ranges[0].Name, Is.EqualTo($"... {30 - 2 * DiffFolding.Context} unchanged lines"));
	}

	[Test]
	public void MapsMemberRegionsBackThroughTheSideLineMap()
	{
		string source = """
			class C
			{
				void M()
				{
					int x = 1;
				}
			}
			""";
		// The side occupies every second document line, as if the other side's lines were
		// interleaved between them.
		var sideToDoc = Enumerable.Range(1, 7).Select(i => i * 2).ToList();

		var ranges = DiffFolding.Members(source, sideToDoc);

		Assert.That(ranges, Is.Not.Empty);
		Assert.That(ranges, Has.All.Matches<FoldRange>(r => r.FromHeaderEnd && !r.DefaultClosed));
		// The type spans source lines 1..7, i.e. document lines 2..14.
		Assert.That(ranges[0].StartLine, Is.EqualTo(2));
		Assert.That(ranges[0].EndLine, Is.EqualTo(14));
	}

	[Test]
	public void IgnoresMemberRegionsThatFallOutsideTheMap()
	{
		Assert.That(DiffFolding.Members("class C\n{\n\tvoid M()\n\t{\n\t}\n}", []), Is.Empty);
	}
}
