using System.Diagnostics;

using Avalonia.Threading;

namespace Stampeded.Editor;

/// <summary>
/// A paint that hands the thread back. Tokenizing a file takes long enough to be seen - a
/// second for a hundred kilobytes, twice that for a file with removed lines, which is painted
/// on both sides - and it happens as the document is shown, while the reader is looking at the
/// forty rows on screen. The first slice runs before the view draws, so those rows are already
/// coloured; the rest follows a slice at a time, between everything else the thread has to do.
///
/// On the UI thread rather than off it: a grammar and the painter over it are one state
/// machine with a cache in front of it, shared by every document of that language, and running
/// two of them at once is a data race. Slices keep the thread answering instead.
/// </summary>
sealed class SlicedPaint
{
	/// <summary>How long one slice may keep the thread. Long enough to paint what is on
	/// screen in the first one, short enough not to be felt.</summary>
	static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(15);

	/// <summary>Spans painted between two readings of the clock. Reading it is not free, and a
	/// span is a few microseconds.</summary>
	const int Between = 128;

	readonly IEnumerator<int> steps;
	readonly Action redraw;
	bool cancelled;

	SlicedPaint(IEnumerator<int> steps, Action redraw)
	{
		this.steps = steps;
		this.redraw = redraw;
	}

	/// <summary>Paints what fits in the first slice, and schedules the rest.</summary>
	public static SlicedPaint Start(IEnumerable<int> paint, Action redraw)
	{
		var sliced = new SlicedPaint(paint.GetEnumerator(), redraw);
		sliced.Pump();
		return sliced;
	}

	/// <summary>Drops what is left of the paint: the document it was painting is gone.</summary>
	public void Cancel()
	{
		if (cancelled)
			return;
		cancelled = true;
		steps.Dispose();
	}

	void Pump()
	{
		if (cancelled)
			return;
		var clock = Stopwatch.StartNew();
		bool more = true;
		for (int n = 1; more; n++)
		{
			more = steps.MoveNext();
			if (n % Between == 0 && clock.Elapsed >= Slice)
				break;
		}
		redraw();
		if (more)
			Dispatcher.UIThread.Post(Pump, DispatcherPriority.Background);
		else
			Cancel();
	}
}
