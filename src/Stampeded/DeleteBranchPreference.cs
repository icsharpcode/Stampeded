namespace Stampeded;

/// <summary>
/// Whether the reader wants the head branch deleted by the merge, kept between sessions in the
/// user data directory next to <see cref="MergeMethodPreference"/>.
///
/// Off unless it is turned on: a merge can be reverted, a deleted branch has to be found again
/// by its sha. Whoever wants the tidying does it once and it stays on, which is the same deal
/// the merge method gets.
/// </summary>
public static class DeleteBranchPreference
{
	const string FileName = "delete-branch.txt";

	public static bool Load() => UserData.Read(FileName) == "true";

	public static void Save(bool value) => UserData.Write(FileName, value ? "true" : "false");
}
