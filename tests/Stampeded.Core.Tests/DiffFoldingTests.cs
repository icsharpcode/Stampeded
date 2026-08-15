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
		// Its header ends at the brace on source line 2, which is document line 4: the header
		// travels through the same map as the range it belongs to.
		Assert.That(ranges[0].HeaderEndLine, Is.EqualTo(4));
	}

	[Test]
	public void IgnoresMemberRegionsThatFallOutsideTheMap()
	{
		Assert.That(DiffFolding.Members("class C\n{\n\tvoid M()\n\t{\n\t}\n}", []), Is.Empty);
	}
}
