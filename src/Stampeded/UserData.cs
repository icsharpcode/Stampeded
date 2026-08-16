namespace Stampeded;

/// <summary>
/// The small settings that outlive a session, one file each under the user data directory:
/// which merge method was chosen, how the tabs stand, how far the window is zoomed.
///
/// Files rather than one document because each is a line long and written by whoever owns it,
/// and a settings format is a thing to migrate. None of them is worth failing anything over:
/// a preference that cannot be read is a preference that was never set.
/// </summary>
static class UserData
{
	static string PathFor(string fileName) => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stampeded", fileName);

	public static string? Read(string fileName)
	{
		try
		{
			string path = PathFor(fileName);
			return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
		}
		catch (IOException)
		{
			return null;
		}
	}

	public static void Write(string fileName, string content)
	{
		try
		{
			string path = PathFor(fileName);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content);
		}
		catch (IOException)
		{
		}
	}
}
