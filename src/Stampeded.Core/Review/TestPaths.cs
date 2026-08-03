namespace Stampeded.Core.Review;

public static class TestPaths
{
	public static bool IsTestPath(string path)
		=> path.Contains("test", StringComparison.OrdinalIgnoreCase);
}
