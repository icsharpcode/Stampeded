namespace Stampeded;

/// <summary>Whether review diffs omit changes made only to whitespace.</summary>
public static class IgnoreWhitespacePreference
{
	const string FileName = "ignore-whitespace.txt";

	public static bool Enabled { get; private set; } = UserData.Read(FileName) == "enabled";

	public static void Set(bool enabled)
	{
		if (Enabled == enabled)
			return;
		Enabled = enabled;
		UserData.Write(FileName, enabled ? "enabled" : "disabled");
	}
}
