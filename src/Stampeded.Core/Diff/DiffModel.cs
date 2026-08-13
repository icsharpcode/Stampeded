namespace Stampeded.Core.Diff;

public enum FileChangeKind
{
	Modified,
	Added,
	Deleted,
	Renamed,
}

/// <summary>Where the two sides of a generated file live. It is not in git, so its content
/// cannot be read out of a commit; either side is null when the file exists only in the
/// other.</summary>
public sealed record GeneratedSource(string? BaseFile, string? HeadFile);

public sealed record FileDiff(
	string OldPath,
	string NewPath,
	FileChangeKind Kind,
	bool IsBinary,
	IReadOnlyList<DiffHunk> Hunks,
	GeneratedSource? Generated = null)
{
	/// <summary>The path to display and key review state by: the new path, except for deletions.</summary>
	public string Path => Kind == FileChangeKind.Deleted ? OldPath : NewPath;

	/// <summary>Whether a build produced this rather than a person committing it. Such a file
	/// has no history to blame, no place on GitHub to carry a comment, and no claim on the
	/// reader's time in the way handwritten code has.</summary>
	public bool IsGenerated => Generated is not null;
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
