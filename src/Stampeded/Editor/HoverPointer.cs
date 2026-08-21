using Avalonia;

namespace Stampeded.Editor;

/// <summary>
/// Whether a pointer event is the pointer actually going somewhere.
///
/// A tooltip is a window of its own, and putting one under the pointer is itself a pointer
/// event on the editor it came from. Treating that as a move closes the tooltip in the same
/// instant it appears and starts the hover timer again, which opens it, which closes it: the
/// log fills with a hover answered every 400 ms and the reader never sees one.
/// </summary>
static class HoverPointer
{
	/// <summary>Far enough that it is a different thing being pointed at, rather than the same
	/// one under a hand that is not perfectly still.</summary>
	const double Tolerance = 2;

	public static bool Moved(Point from, Point to)
		=> Math.Abs(from.X - to.X) > Tolerance || Math.Abs(from.Y - to.Y) > Tolerance;
}
