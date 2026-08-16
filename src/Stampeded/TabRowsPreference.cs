namespace Stampeded;

/// <summary>
/// Whether the document tabs stand in one scrolling row or wrap onto several, kept between
/// sessions in the user data directory.
///
/// A review opens a tab per file, and a pass over twenty files leaves twenty tabs: in one row
/// the ones that matter scroll out of sight, and finding a tab again costs more than opening
/// the file again did. Which of the two layouts is right depends on the reader's screen, so it
/// is theirs to set and worth remembering.
/// </summary>
public static class TabRowsPreference
{
	static string FilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stampeded", "tab-rows.txt");

	/// <summary>True while the tabs wrap onto as many rows as they need.</summary>
	public static bool MultiRow { get; private set; } = Load();

	/// <summary>Raised when the layout changes, for the strips that wear it.</summary>
	public static event Action? Changed;

	public static void Set(bool multiRow)
	{
		if (MultiRow == multiRow)
			return;
		MultiRow = multiRow;
		Save(multiRow);
		Changed?.Invoke();
	}

	static bool Load()
	{
		try
		{
			return File.Exists(FilePath) && File.ReadAllText(FilePath).Trim() == "multi";
		}
		catch (IOException)
		{
			return false;
		}
	}

	static void Save(bool multiRow)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, multiRow ? "multi" : "single");
		}
		catch (IOException)
		{
			// A layout is a convenience; never fail over failing to remember it.
		}
	}
}
