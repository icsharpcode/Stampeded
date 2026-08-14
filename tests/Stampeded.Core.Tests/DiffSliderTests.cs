using NUnit.Framework;

using Stampeded.Core.Diff;

namespace Stampeded.Core.Tests;

public class DiffSliderTests
{
	// A class with one method, and the same class with a second method added after it. The
	// insertion can be cut in two places: at A's closing brace, or at the blank line.
	static readonly string[] Before = ["class C", "{", "\tvoid A()", "\t{", "\t}", "}"];
	static readonly string[] After =
		["class C", "{", "\tvoid A()", "\t{", "\t}", "", "\tvoid B()", "\t{", "\t}", "}"];

	[Test]
	public void MovesAnInsertionOffTheClosingBraceOfTheMethodBefore()
	{
		// Starts at "\t}" - A's brace reads as inserted, and B's as unchanged.
		List<DiffRun> runs = [new(true, 4, 4), new(false, 0, 4), new(true, 2, 2)];

		var slid = DiffSlider.Shift(Before, After, runs);

		// One line later: the run now begins at the blank line between the methods.
		Assert.That(slid, Is.EqualTo(new List<DiffRun> {
			new(true, 5, 5), new(false, 0, 4), new(true, 1, 1),
		}));
	}

	[Test]
	public void LeavesAnInsertionThatAlreadyStartsAtABoundary()
	{
		List<DiffRun> runs = [new(true, 5, 5), new(false, 0, 4), new(true, 1, 1)];

		Assert.That(DiffSlider.Shift(Before, After, runs), Is.EqualTo(runs));
	}

	[Test]
	public void MovesADeletionTheSameWay()
	{
		// The mirror image: the method is removed, so the run lives on the old side.
		List<DiffRun> runs = [new(true, 4, 4), new(false, 4, 0), new(true, 2, 2)];

		var slid = DiffSlider.Shift(After, Before, runs);

		Assert.That(slid, Is.EqualTo(new List<DiffRun> {
			new(true, 5, 5), new(false, 4, 0), new(true, 1, 1),
		}));
	}

	[Test]
	public void LeavesARunWithNothingBetterToMoveTo()
	{
		// An item appended to a list: every position is a line of the same shape and depth,
		// so none of them reads better than the one the aligner chose.
		string[] before = ["items = [", "\t\"a\",", "]"];
		string[] after = ["items = [", "\t\"a\",", "\t\"a\",", "]"];
		List<DiffRun> runs = [new(true, 2, 2), new(false, 0, 1), new(true, 1, 1)];

		Assert.That(DiffSlider.Shift(before, after, runs), Is.EqualTo(runs));
	}

	[Test]
	public void LeavesAReplacementAlone()
	{
		// A run standing against lines on the other side is anchored by them; sliding it
		// would change which lines it claims to replace.
		List<DiffRun> runs = [new(true, 4, 4), new(false, 1, 4), new(true, 1, 2)];

		Assert.That(DiffSlider.Shift(Before, After, runs), Is.EqualTo(runs));
	}
}
