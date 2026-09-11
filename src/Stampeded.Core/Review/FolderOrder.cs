namespace Stampeded.Core.Review;

/// <summary>
/// Keeps the files of a directory together in a list that was sorted by something else.
///
/// The changed-file list is a tree: it groups paths by directory whatever order they arrive in,
/// and puts a directory where its first file would have been. A flat order that leaves a
/// directory and comes back to it - which is what "tests first", "touched since the last pass
/// first" and "generated output last" each do on their own - is an order that tree cannot show,
/// and the keys that walk the list then walk it in an order the reader is not looking at.
/// Grouping the order the same way the tree does is what keeps one list from being two.
/// </summary>
public static class FolderOrder
{
	/// <summary>The files regrouped by directory: a directory sits where its first file sat,
	/// and within one the files keep the order they came in.</summary>
	public static IEnumerable<T> ByFolder<T>(IEnumerable<T> files, Func<T, string> path)
		=> ByFolder(files, path, 0);

	static IEnumerable<T> ByFolder<T>(IEnumerable<T> files, Func<T, string> path, int depth)
		=> files
			// Every member of a group holding more than one file shares this segment as a
			// directory - a file and a directory cannot share a name side by side - so there
			// is always a further segment for the next level to group by.
			.GroupBy(f => path(f).Split('/')[depth])
			.SelectMany(g => g.Skip(1).Any() ? ByFolder(g, path, depth + 1) : g);
}
