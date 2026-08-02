namespace Stampeded.Core.Diff;

public enum FileChangeKind
{
	Modified,
	Added,
	Deleted,
	Renamed,
}

public sealed record FileDiff(
	string OldPath,
	string NewPath,
	FileChangeKind Kind,
	bool IsBinary,
	IReadOnlyList<DiffHunk> Hunks)
{
	/// <summary>The path to display and key review state by: the new path, except for deletions.</summary>
	public string Path => Kind == FileChangeKind.Deleted ? OldPath : NewPath;
}

public sealed record DiffHunk(
	int OldStart,
	int OldLength,
	int NewStart,
	int NewLength,
	string Header,
	IReadOnlyList<PatchLine> Lines);

public enum PatchLineKind
{
	Context,
	Added,
	Removed,
}

public sealed record PatchLine(PatchLineKind Kind, string Text);
