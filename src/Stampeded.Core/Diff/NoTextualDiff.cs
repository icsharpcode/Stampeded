using System.Globalization;

namespace Stampeded.Core.Diff;

/// <summary>
/// What to say about a file the diff has nothing to show for.
///
/// Two kinds reach a review with no lines in them: a binary file, which git compares byte by
/// byte and reports only as differing, and a file that changed without its content changing -
/// a rename, or a permission bit. Both used to open as an empty document, which reads as a
/// tool that failed rather than as a file with nothing to read, and the file list showed them
/// with no counts at all beside files that genuinely changed nothing.
/// </summary>
public static class NoTextualDiff
{
	/// <summary>Whether this file has no lines to show. A generated file is never one of
	/// these: its two sides are read from disk and compared here, so it either has hunks or
	/// was left out of the change altogether.</summary>
	public static bool Applies(FileDiff file) => file.IsBinary || file.Hunks.Count == 0;

	/// <summary>The badge the file list carries for such a file, or empty for one with lines.
	/// Short, because it stands where the line counts would.</summary>
	public static string Badge(FileDiff file)
		=> !Applies(file) ? "" : file.IsBinary ? "binary" : "no lines";

	/// <summary>
	/// The page shown in place of the diff. Sizes are what a reader actually wants to know
	/// about a binary change - whether the image got bigger - and are null for the side that
	/// does not have the file.
	/// </summary>
	public static string Describe(FileDiff file, long? baseSize, long? headSize)
	{
		var text = new System.Text.StringBuilder();
		text.Append(file.Path).Append("\n\n");
		text.Append(file.IsBinary
			? "Binary file: git compares the bytes and reports only that they differ, so there\n"
				+ "is no textual diff to read.\n\n"
			: "No lines changed. A file reaches a change without any when it was renamed, or\n"
				+ "when only its permissions moved.\n\n");
		text.Append(Kind(file));
		if (file.Kind == FileChangeKind.Renamed)
			text.Append(" from ").Append(file.OldPath);
		text.Append('.');
		if (Sizes(baseSize, headSize) is { Length: > 0 } sizes)
			text.Append("  ").Append(sizes);
		return text.Append('\n').ToString();
	}

	static string Kind(FileDiff file) => file.Kind switch {
		FileChangeKind.Added => "Added",
		FileChangeKind.Deleted => "Deleted",
		FileChangeKind.Renamed => "Renamed",
		_ => "Modified",
	};

	/// <summary>
	/// How the two sides compare, said the way a reader would: one size for a file that only
	/// one revision has, and the difference spelled out for one both have - "grew by 2.5 KB"
	/// is the fact, where two absolute numbers are the arithmetic to get there.
	/// </summary>
	static string Sizes(long? baseSize, long? headSize) => (baseSize, headSize) switch {
		(null, { } head) => Size(head),
		({ } start, null) => "was " + Size(start),
		({ } start, { } end) when start == end => Size(end) + ", unchanged in size",
		({ } start, { } end) => $"{Size(start)} -> {Size(end)} ({(end > start ? "+" : "-")}{Size(Math.Abs(end - start))})",
		_ => "",
	};

	/// <summary>A byte count as a person reads one. Binary in the sense the file managers use,
	/// because that is what the reader will see if they check.</summary>
	public static string Size(long bytes)
	{
		string[] units = ["bytes", "KB", "MB", "GB"];
		double size = bytes;
		int unit = 0;
		while (size >= 1024 && unit < units.Length - 1)
		{
			size /= 1024;
			unit++;
		}
		// Whole bytes: a file of 512.0 bytes is 512 bytes, and the decimal is noise.
		return unit == 0
			? string.Create(CultureInfo.InvariantCulture, $"{bytes} {units[0]}")
			: string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
	}
}
