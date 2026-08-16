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

	const string FileName = "merge-method.txt";

	public static string Load() => UserData.Read(FileName) is { Length: > 0 } method ? method : Default;

	public static void Save(string method) => UserData.Write(FileName, method);
}
