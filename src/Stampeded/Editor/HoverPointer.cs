using Avalonia;

using AvaloniaEdit;
using AvaloniaEdit.Document;

namespace Stampeded.Editor;

/// <summary>
/// Whether a pointer event means the reader is pointing at something else.
///
/// Asked of the text rather than of the coordinates, for two reasons. A tooltip is a window
/// of its own, and putting one under the pointer is itself a pointer event on the editor it
/// came from: read as a move, it closes the tooltip in the instant it appears and starts the
/// hover timer, which opens it again - the log fills with a hover answered every 400 ms and
/// nothing is ever readable. And a few pixels are not a fixed amount of text: the same
/// character under a hand that is not perfectly still is what must not count as a move, while
/// two pixels across a line boundary is another line, and a tooltip left over from the line
/// above is worse than none.
/// </summary>
static class HoverPointer
{
	public static bool PointsElsewhere(TextEditor editor, Point point, ref TextLocation? last)
	{
		var at = editor.GetPositionFromPoint(point)?.Location;
		if (at.Equals(last))
			return false;
		last = at;
		return true;
	}
}
