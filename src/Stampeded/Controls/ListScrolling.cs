using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Stampeded.Controls;

public static class ListScrolling
{
	/// <summary>
	/// Reveals a row by setting the offset its index implies, rather than calling
	/// <see cref="ItemsControl.ScrollIntoView(int)"/>.
	///
	/// That method realises a container for the target, arranges it at its own desired width,
	/// parks it aside for the duration of a few layout passes and then drops the reference. A
	/// container the passes do not adopt back into the realized range is left a visible child
	/// of the panel that nothing arranges again -- it keeps painting its old item at its old
	/// position and its own narrow width, over whatever row now occupies that spot. The rows
	/// in these lists are a uniform height, so the arithmetic here reaches the same place
	/// without ever entering that code path.
	///
	/// <see cref="TreeView.SharpTreeView"/> carries its own copy of this: it is vendored from
	/// ILSpy and has to stay portable back to it.
	/// </summary>
	public static void ScrollRowIntoView(this ListBox list, int index)
	{
		if (index < 0 || index >= list.ItemCount)
			return;
		if (list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scrollViewer)
			return;

		// Items may have just been added; the extent has to catch up before an offset can be
		// clamped against it.
		list.UpdateLayout();

		double rowHeight = RowHeight(list);
		if (rowHeight <= 0)
			return;
		double viewport = scrollViewer.Viewport.Height;
		double rowTop = index * rowHeight;
		double offset = scrollViewer.Offset.Y;

		if (rowTop < offset)
			offset = rowTop;
		else if (rowTop + rowHeight > offset + viewport)
			offset = rowTop + rowHeight - viewport;
		else
			return;

		double maxOffset = Math.Max(0, scrollViewer.Extent.Height - viewport);
		scrollViewer.Offset = new Vector(scrollViewer.Offset.X, Math.Clamp(offset, 0, maxOffset));
	}

	/// <summary>Reveals the row of <paramref name="item"/>, if the list has it.</summary>
	public static void ScrollRowIntoView(this ListBox list, object item)
		=> list.ScrollRowIntoView(list.Items.IndexOf(item));

	/// <summary>The height of a row, taken from one that exists rather than assumed. Zero
	/// when nothing is realized yet, which is also when there is nothing to scroll.</summary>
	static double RowHeight(ListBox list)
	{
		foreach (var container in list.GetRealizedContainers())
		{
			if (container.Bounds.Height > 0)
				return container.Bounds.Height;
		}
		return 0;
	}
}
