namespace Stampeded;

/// <summary>
/// The merge method the reader last chose, kept between sessions in the user data directory.
///
/// A merge commit is the default rather than a squash: this tool exists to read a change as the
/// series of commits it was written as, and squashing throws that series away at the moment it
/// lands. Repositories that want it squashed still offer it one selection away - and that
/// selection is what is remembered, so the choice is made once per person, not once per merge.
/// </summary>
public static class MergeMethodPreference
{
	public const string Default = "merge";

	static string FilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stampeded", "merge-method.txt");

	public static string Load()
	{
		try
		{
			return File.Exists(FilePath) && File.ReadAllText(FilePath).Trim() is { Length: > 0 } method
				? method
				: Default;
		}
		catch (IOException)
		{
			return Default;
		}
	}

	public static void Save(string method)
	{
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllText(FilePath, method);
		}
		catch (IOException)
		{
			// A preference is a convenience; never fail a merge over failing to remember it.
		}
	}
}
