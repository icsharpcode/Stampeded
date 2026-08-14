namespace Stampeded.Core.Diff;

/// <summary>One run of the alignment: matched lines, or lines present on one side only.</summary>
public sealed record DiffRun(bool IsMatch, int OldLength, int NewLength);

/// <summary>
/// Moves an inserted or deleted run to where a reader would have cut it.
///
/// When a run's first line repeats immediately after it, the run can start a line later and
/// still describe the same change - the diff is ambiguous, and the aligner's choice is
/// arbitrary. That is how an added method comes out starting at the closing brace of the
/// method before it and ending inside itself: valid, and unreadable.
///
/// A run only moves to a position that is plainly better than the one it has: to a paragraph
/// boundary when it does not sit on one, or to a shallower indentation - a block that starts
/// where the code steps outward reads as a block. Positions of equal standing leave it where
/// the aligner put it, because a diff that is merely different is worse than one that is
/// familiar.
/// </summary>
public static class DiffSlider
{
	public static List<DiffRun> Shift(
		IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, IReadOnlyList<DiffRun> runs)
	{
		var shifted = runs.ToList();
		for (int i = 1; i + 1 < shifted.Count; i++)
		{
			var run = shifted[i];
			// Only a run on one side alone can slide: a replacement is anchored by the lines
			// it stands against. Its neighbours have to be matches, since sliding trades
			// lines with them.
			bool insert = !run.IsMatch && run.OldLength == 0 && run.NewLength > 0;
			bool delete = !run.IsMatch && run.NewLength == 0 && run.OldLength > 0;
			if ((!insert && !delete) || !shifted[i - 1].IsMatch || !shifted[i + 1].IsMatch)
				continue;

			var lines = insert ? newLines : oldLines;
			int start = Offset(shifted, i, insert);
			int length = insert ? run.NewLength : run.OldLength;
			int room = insert ? shifted[i - 1].NewLength : shifted[i - 1].OldLength;
			int roomBelow = insert ? shifted[i + 1].NewLength : shifted[i + 1].OldLength;
			int shift = BestShift(lines, start, length, MaxUp(lines, start, length, room),
				MaxDown(lines, start, length, roomBelow));
			if (shift == 0)
				continue;
			// The run takes lines from one neighbour and gives them to the other; both are
			// matches, so both sides move together.
			shifted[i - 1] = shifted[i - 1] with {
				OldLength = shifted[i - 1].OldLength + shift,
				NewLength = shifted[i - 1].NewLength + shift,
			};
			shifted[i + 1] = shifted[i + 1] with {
				OldLength = shifted[i + 1].OldLength - shift,
				NewLength = shifted[i + 1].NewLength - shift,
			};
		}
		return shifted;
	}

	static int Offset(List<DiffRun> runs, int index, bool newSide)
	{
		int offset = 0;
		for (int i = 0; i < index; i++)
			offset += newSide ? runs[i].NewLength : runs[i].OldLength;
		return offset;
	}

	/// <summary>How far the run can start earlier: each step moves its last line out and the
	/// line before it in, which only describes the same change while those are equal.</summary>
	static int MaxUp(IReadOnlyList<string> lines, int start, int length, int room)
	{
		int steps = 0;
		while (steps < room && start - steps - 1 >= 0
			&& lines[start - steps - 1] == lines[start + length - steps - 1])
		{
			steps++;
		}
		return steps;
	}

	static int MaxDown(IReadOnlyList<string> lines, int start, int length, int room)
	{
		int steps = 0;
		while (steps < room && start + length + steps < lines.Count
			&& lines[start + length + steps] == lines[start + steps])
		{
			steps++;
		}
		return steps;
	}

	static int BestShift(IReadOnlyList<string> lines, int start, int length, int up, int down)
	{
		var here = Rank(lines, start);
		int best = 0;
		var bestRank = here;
		for (int shift = -up; shift <= down; shift++)
		{
			if (shift == 0)
				continue;
			var rank = Rank(lines, start + shift);
			// Strictly better only: an equal position is not a reason to move.
			if (rank.CompareTo(bestRank) < 0)
			{
				bestRank = rank;
				best = shift;
			}
		}
		return best;
	}

	/// <summary>How good a starting line is, lower being better: a paragraph boundary first,
	/// then the shallower indentation.</summary>
	static (int NotAtBoundary, int Indent) Rank(IReadOnlyList<string> lines, int start)
		=> (StartsParagraph(lines, start) ? 0 : 1, Indent(lines, start));

	static bool StartsParagraph(IReadOnlyList<string> lines, int start)
		=> start == 0 || IsBlank(lines[start - 1]) || IsBlank(lines[start]);

	static int Indent(IReadOnlyList<string> lines, int start)
	{
		if (start >= lines.Count || IsBlank(lines[start]))
			return 0;
		string line = lines[start];
		int indent = 0;
		while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t'))
			indent++;
		return indent;
	}

	static bool IsBlank(string line) => line.AsSpan().Trim().Length == 0;
}
