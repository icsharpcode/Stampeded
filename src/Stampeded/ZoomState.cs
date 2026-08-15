using Avalonia.Media;

namespace Stampeded;

/// <summary>
/// The window zoom, in a form the application styles can reach.
///
/// The window scales through a <c>LayoutTransformControl</c> around its content, and a popup is
/// not inside that content: a context menu, a tooltip or the comment editor is hosted by the
/// window's overlay layer, above it. So they have to be scaled where they are, and a style
/// needs a transform it can name without a data context.
///
/// This one is a render transform, not a layout one. A popup is sized to what it holds and
/// nothing wraps around it, so laying it out at the scaled size would gain nothing over drawing
/// it there - and text drawn through a scale is still rasterised at the size it ends up.
/// </summary>
public static class ZoomState
{
	public static ScaleTransform PopupScale { get; } = new();

	public static void Set(double zoom)
	{
		PopupScale.ScaleX = zoom;
		PopupScale.ScaleY = zoom;
	}
}
