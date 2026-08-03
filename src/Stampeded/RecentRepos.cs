namespace Stampeded;

/// <summary>
/// Most-recently-opened repository paths, one per line under the user data directory.
/// </summary>
public static class RecentRepos
{
	const int Capacity = 10;

	static string FilePath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stampeded", "recent-repos.txt");

	public static IReadOnlyList<string> Load()
	{
		try
		{
			return File.Exists(FilePath)
				? File.ReadAllLines(FilePath).Where(Directory.Exists).ToList()
				: [];
		}
		catch (IOException)
		{
			return [];
		}
	}

	public static void Record(string repoPath)
	{
		var list = Load().ToList();
		list.RemoveAll(p => p == repoPath);
		list.Insert(0, repoPath);
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
			File.WriteAllLines(FilePath, list.Take(Capacity));
		}
		catch (IOException)
		{
			// Recents are a convenience; never fail an open over them.
		}
	}
}
