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

	/// <summary>A declaration in document lines: where it starts, where it ends, and where its
	/// header - the part that is worth reading on its own - stops.</summary>
	static FoldRange Decl(int start, int end, int headerEnd)
		=> new(start, end, " ... ", DefaultClosed: false, FromHeaderEnd: true, headerEnd);

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
	public void LeavesARunOfFiveLinesOrFewerAlone()
	{
		// Two hunks with fifteen unchanged lines between them: ten of those stay visible as the
		// context beside each hunk, and the five left over are read rather than hidden - a
		// control that saves five lines costs more attention than the five lines do.
		var tags = Tags("+" + new string('.', 2 * ContextGaps.Context + 5) + "+");
		Assert.That(ContextGaps.Compute(tags, hasChanges: true), Is.Empty);

		// One line more, and there is enough behind the control to be worth it.
		var wider = Tags("+" + new string('.', 2 * ContextGaps.Context + ContextGaps.MinHidden) + "+");
		Assert.That(ContextGaps.Compute(wider, hasChanges: true).Single().HiddenCount,
			Is.EqualTo(ContextGaps.MinHidden));
	}

	[Test]
	public void CutsARunAroundTheSignatureTheChangeIsUnder()
	{
		// Forty unchanged lines, then a change: without any structure the run hides everything
		// up to the hunk's own context.
		var tags = Tags(new string('.', 40) + "+" + new string('.', 12));
		Assert.That(ContextGaps.Compute(tags, hasChanges: true)[0].LastLine,
			Is.EqualTo(40 - ContextGaps.Context));

		// A member declared at line 30, whose header ends on 31, holding the change: the run
		// above it stays hidden, the signature is shown, and the four lines between it and the
		// hunk's context are too few to be worth a control of their own.
		var gaps = ContextGaps.Compute(tags, hasChanges: true, [Decl(30, 45, 31)]);

		Assert.That(gaps[0], Is.EqualTo(new ContextGap(1, 29)));
		Assert.That(gaps[1].FirstLine, Is.EqualTo(42 + ContextGaps.Context));
	}

	[Test]
	public void HidesTheLinesBetweenASignatureAndTheHunkWhenThereAreEnoughOfThem()
	{
		var tags = Tags(new string('.', 40) + "+" + new string('.', 10));

		// The signature sits on line 20, so fifteen lines of body lie between it and the
		// hunk's context - a run of its own, and not one the reader has to scroll past.
		var gaps = ContextGaps.Compute(tags, hasChanges: true, [Decl(20, 45, 20)]);

		Assert.That(gaps[0], Is.EqualTo(new ContextGap(1, 19)));
		Assert.That(gaps[1], Is.EqualTo(new ContextGap(21, 40 - ContextGaps.Context)));
	}

	[Test]
	public void PullsOutEveryEnclosingHeaderNotOnlyTheInnermost()
	{
		var tags = Tags(new string('.', 40) + "+" + new string('.', 10));

		// A type over the whole file with a member inside it: both say what the change is part
		// of, so both headers are shown, however far above they sit.
		var gaps = ContextGaps.Compute(tags, hasChanges: true, [Decl(2, 51, 3), Decl(30, 45, 31)]);

		Assert.That(gaps[0], Is.EqualTo(new ContextGap(4, 29)),
			"the one line above the type header is not worth a control");
		Assert.That(gaps.Any(g => g.Contains(2) || g.Contains(3) || g.Contains(30) || g.Contains(31)),
			Is.False, "the headers themselves are shown");
	}

	[Test]
	public void LeavesADeclarationTheChangeIsNotInsideAlone()
	{
		var tags = Tags(new string('.', 40) + "+" + new string('.', 10));

		// A member that ends above the change says nothing about it, however close it sits.
		var gaps = ContextGaps.Compute(tags, hasChanges: true, [Decl(10, 20, 11)]);

		Assert.That(gaps[0], Is.EqualTo(new ContextGap(1, 40 - ContextGaps.Context)));
	}

	[Test]
	public void KeepsAHeaderThatStartsAboveTheRunVisibleToItsEnd()
	{
		// The change is on the first line, so the run below it starts inside the header of the
		// declaration holding both.
		var tags = Tags("+" + new string('.', 40) + "+");

		var gaps = ContextGaps.Compute(tags, hasChanges: true, [Decl(1, 42, 8)]);

		Assert.That(gaps, Has.Count.EqualTo(1));
		Assert.That(gaps[0].FirstLine, Is.EqualTo(9), "the rest of the signature is shown");
	}

	[Test]
	public void PlacesNoGapBetweenHeadersThatTouch()
	{
		var tags = Tags(new string('.', 40) + "+" + new string('.', 10));

		// A type whose header ends on line 10 and a member declared on line 11: nothing lies
		// between them to hide.
		var gaps = ContextGaps.Compute(tags, hasChanges: true, [Decl(2, 51, 10), Decl(11, 45, 11)]);

		Assert.That(gaps[0], Is.EqualTo(new ContextGap(12, 40 - ContextGaps.Context)));
		Assert.That(gaps.Zip(gaps.Skip(1)).All(p => p.First.LastLine < p.Second.FirstLine), Is.True,
			"gaps stay ordered and disjoint");
	}

	[Test]
	public void ShowsAWholeRunWhoseFragmentsAreAllTooSmall()
	{
		// A short run with a header near its top: what is left on either side of that header is
		// less than a control is worth, so the run is simply read.
		var tags = Tags(new string('.', 12) + "+" + new string('.', 12));

		var gaps = ContextGaps.Compute(tags, hasChanges: true, [Decl(4, 20, 5)]);

		Assert.That(gaps.Any(g => g.FirstLine == 1), Is.False);
		Assert.That(gaps, Has.Count.EqualTo(1), "only the run after the hunk is left");
	}

	[Test]
	public void HidesToTheEndOfTheFileEvenWithADeclarationInTheRun()
	{
		// A change, then fifteen unchanged lines to the end of the file, the last member among
		// them. A declaration starting below the change cannot hold it, so it says nothing
		// about it; keeping its header would spell out the tail of the file for nothing.
		var tags = Tags("+" + new string('.', 15));

		var gaps = ContextGaps.Compute(tags, hasChanges: true, [Decl(8, 16, 8)]);

		Assert.That(gaps, Has.Count.EqualTo(1));
		Assert.That(gaps[0].FirstLine, Is.EqualTo(2 + ContextGaps.Context));
		Assert.That(gaps[0].LastLine, Is.EqualTo(16));
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
