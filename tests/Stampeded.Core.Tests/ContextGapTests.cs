using NUnit.Framework;

using Stampeded.Core.Diff;

namespace Stampeded.Core.Tests;

public class ContextGapTests
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
	public void HidesNothingWhenTheDocumentHasNoChanges()
	{
		Assert.That(ContextGaps.Compute(Tags(new string('.', 50)), hasChanges: false), Is.Empty);
	}

	[Test]
	public void KeepsContextLinesVisibleBesideAHunk()
	{
		// 20 unchanged, one added line, 20 unchanged.
		var gaps = ContextGaps.Compute(Tags(new string('.', 20) + "+" + new string('.', 20)), hasChanges: true);

		Assert.That(gaps, Has.Count.EqualTo(2));
		// Leading run: hides from line 1 (document edge) to 3 lines before the hunk.
		Assert.That(gaps[0].FirstLine, Is.EqualTo(1));
		Assert.That(gaps[0].LastLine, Is.EqualTo(20 - ContextGaps.Context));
		// Trailing run: starts 3 lines after the hunk and hides to the last line.
		Assert.That(gaps[1].FirstLine, Is.EqualTo(22 + ContextGaps.Context));
		Assert.That(gaps[1].LastLine, Is.EqualTo(41));
	}

	[Test]
	public void SkipsRunsTooShortToBeWorthHiding()
	{
		// A run shorter than the context kept on both sides of it has nothing left to hide.
		Assert.That(ContextGaps.Compute(Tags("+" + new string('.', 2 * ContextGaps.Context) + "+"),
			hasChanges: true), Is.Empty);
	}

	[Test]
	public void RevealsFromEitherEndAStepAtATime()
	{
		var gaps = ContextGaps.Compute(Tags("+" + new string('.', 60) + "+"), hasChanges: true);
		var gap = gaps.Single();
		int hidden = 60 - 2 * ContextGaps.Context;
		Assert.That(gap.HiddenCount, Is.EqualTo(hidden));

		// The top step reveals the lines below the hunk above: the gap starts later.
		var afterTop = ContextGaps.RevealTop(gap, ContextGaps.Step)!;
		Assert.That(afterTop.FirstLine, Is.EqualTo(gap.FirstLine + ContextGaps.Step));
		Assert.That(afterTop.LastLine, Is.EqualTo(gap.LastLine));

		// The bottom step reveals the lines leading into the hunk below: it ends earlier.
		var afterBottom = ContextGaps.RevealBottom(gap, ContextGaps.Step)!;
		Assert.That(afterBottom.LastLine, Is.EqualTo(gap.LastLine - ContextGaps.Step));
		Assert.That(afterBottom.FirstLine, Is.EqualTo(gap.FirstLine));
	}

	[Test]
	public void AStepThatWouldOpenTheWholeGapClosesItInstead()
	{
		var gap = new ContextGap(10, 14); // five hidden lines
		Assert.That(ContextGaps.RevealTop(gap, 5), Is.Null);
		Assert.That(ContextGaps.RevealBottom(gap, 20), Is.Null);
		Assert.That(ContextGaps.RevealTop(gap, 4), Is.EqualTo(new ContextGap(14, 14)));
	}

	[Test]
	public void KnowsWhichLinesItHides()
	{
		var gap = new ContextGap(10, 14);
		Assert.That(gap.Contains(9), Is.False);
		Assert.That(gap.Contains(10), Is.True);
		Assert.That(gap.Contains(14), Is.True);
		Assert.That(gap.Contains(15), Is.False);
	}
}
