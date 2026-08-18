namespace Stampeded;

/// <summary>
/// Whether a file's change is read as one interleaved document or as two panes, kept between
/// sessions in the user data directory.
///
/// It is a setting rather than a per-file command because it is a way of reading, not a
/// property of a file: a reader who wants two panes wants them for the next file too. One tab
/// per file either way - the layouts are two views of the same thing, not one of them plus an
/// extra.
/// </summary>
public static class DiffLayoutPreference
{
	const string FileName = "diff-layout.txt";

	/// <summary>True while a change opens as two panes.</summary>
	public static bool SideBySide { get; private set; } = UserData.Read(FileName) == "side-by-side";

	/// <summary>Raised when the layout changes, for the documents that have to be rebuilt in
	/// it.</summary>
	public static event Action? Changed;

	public static void Set(bool sideBySide)
	{
		if (SideBySide == sideBySide)
			return;
		SideBySide = sideBySide;
		UserData.Write(FileName, sideBySide ? "side-by-side" : "unified");
		Changed?.Invoke();
	}
}
