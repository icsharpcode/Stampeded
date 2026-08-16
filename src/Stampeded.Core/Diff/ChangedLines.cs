namespace Stampeded.Core.Diff;

/// <summary>
/// Which lines of a change each file contributes, per side: the ones it adds and removes,
/// and the ones a review comment can be attached to at all - which is every line a hunk
/// prints on that side, context included, because that is what GitHub will take a comment on.
///
/// One walk over the hunks answers all four. A hunk carries its own starting line numbers and
/// nothing else does, so following them is the only way to turn a run of +/- markers into
/// numbers - and doing that walk in more than one place is how two answers about the same
/// diff come to disagree.
/// </summary>
public sealed class ChangedLines
{
	static readonly HashSet<int> None = [];

	readonly Dictionary<string, HashSet<int>> added = new(StringComparer.Ordinal);
	readonly Dictionary<string, HashSet<int>> removed = new(StringComparer.Ordinal);
	readonly Dictionary<string, HashSet<int>> newSide = new(StringComparer.Ordinal);
	readonly Dictionary<string, HashSet<int>> oldSide = new(StringComparer.Ordinal);

	/// <summary>No change at all - what a workspace with no review open has.</summary>
	public static ChangedLines Empty { get; } = new();

	public static ChangedLines From(IEnumerable<FileDiff> files)
	{
		var index = new ChangedLines();
		foreach (var file in files)
		{
			// Keyed by the path each side knows the file under: a rename has two, and a
			// deletion is only ever asked about by its old one.
			var addedLines = index.added[file.Path] = [];
			var removedLines = index.removed[file.OldPath] = [];
			var newLines = index.newSide[file.Path] = [];
			var oldLines = index.oldSide[file.OldPath] = [];
			foreach (var hunk in file.Hunks)
			{
				int newLine = hunk.NewStart, oldLine = hunk.OldStart;
				foreach (var line in hunk.Lines)
				{
					if (line.Kind != PatchLineKind.Removed)
					{
						newLines.Add(newLine);
						if (line.Kind == PatchLineKind.Added)
							addedLines.Add(newLine);
						newLine++;
					}
					if (line.Kind != PatchLineKind.Added)
					{
						oldLines.Add(oldLine);
						if (line.Kind == PatchLineKind.Removed)
							removedLines.Add(oldLine);
						oldLine++;
					}
				}
			}
		}
		return index;
	}

	/// <summary>New-file lines the change adds to a file.</summary>
	public IReadOnlySet<int> Added(string path) => Get(added, path);

	/// <summary>Old-file lines the change removes, by the path the base knows the file under.</summary>
	public IReadOnlySet<int> Removed(string oldPath) => Get(removed, oldPath);

	/// <summary>Every new-side line the diff prints for a file - what a comment on the right
	/// side can land on.</summary>
	public IReadOnlySet<int> CommentableNew(string path) => Get(newSide, path);

	/// <summary>Every old-side line the diff prints - what a comment on the left side can
	/// land on.</summary>
	public IReadOnlySet<int> CommentableOld(string oldPath) => Get(oldSide, oldPath);

	public bool IsAdded(string path, int newLine) => Get(added, path).Contains(newLine);

	/// <summary>The added lines of every file, for what is measured across the whole change.</summary>
	public IEnumerable<(string Path, IReadOnlySet<int> Lines)> AddedByFile
		=> added.Select(entry => (entry.Key, (IReadOnlySet<int>)entry.Value));

	static IReadOnlySet<int> Get(Dictionary<string, HashSet<int>> index, string path)
		=> index.TryGetValue(path, out var lines) ? lines : None;
}
