namespace Stampeded.Core.Tests;

/// <summary>Removes a directory a test created under the temp folder. git writes its loose
/// objects read-only, and on Windows Directory.Delete refuses a read-only file, so the flag is
/// cleared first. Removal is best effort: a directory that cannot go is a leak in the temp
/// folder, not a verdict on what the test asserted.</summary>
static class TempDirectory
{
	public static void Delete(string dir)
	{
		if (!Directory.Exists(dir))
			return;
		try
		{
			// Reparse points are skipped so a symlink a test planted is not followed out of
			// the directory being cleaned.
			var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
			foreach (var file in Directory.EnumerateFiles(dir, "*", options))
				File.SetAttributes(file, FileAttributes.Normal);
			Directory.Delete(dir, recursive: true);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}
}
