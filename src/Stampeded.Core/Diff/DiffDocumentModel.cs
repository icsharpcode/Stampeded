namespace Stampeded.Core.Diff;

public enum DiffLineKind
{
	Context,
	Added,
	Removed,
	Filler,
}

/// <summary>A changed character range within one document line (line-relative offsets).</summary>
public readonly record struct IntraLineSpan(int Start, int Length);

/// <summary>
/// Per-document-line diff metadata. <see cref="OldLine"/>/<see cref="NewLine"/> are 1-based
/// blob line numbers, 0 when the line does not exist on that side.
/// </summary>
public readonly record struct DiffLineTag(
	DiffLineKind Kind,
	int OldLine,
	int NewLine,
	IReadOnlyList<IntraLineSpan>? WordDiffs);

/// <summary>A maximal run of non-context document lines (1-based, inclusive).</summary>
public readonly record struct HunkSpan(int FirstDocLine, int LastDocLine);

/// <summary>
/// The unified-diff document: the full NEW file text with REMOVED lines interleaved as
/// verbatim old-blob lines. Every document line is a verbatim copy of a blob line, so
/// (docLine, column) maps exactly to (blobLine, column) on whichever side the line exists.
/// All position translation between the editor and the old/new blobs goes through this map.
/// </summary>
public sealed class DiffDocumentModel
{
	public required string Text { get; init; }
	public required IReadOnlyList<DiffLineTag> Tags { get; init; }
	public required IReadOnlyList<HunkSpan> Hunks { get; init; }

	readonly Lazy<Dictionary<int, int>> newToDoc;
	readonly Lazy<Dictionary<int, int>> oldToDoc;

	public DiffDocumentModel()
	{
		newToDoc = new(() => BuildIndex(t => t.NewLine));
		oldToDoc = new(() => BuildIndex(t => t.OldLine));
	}

	Dictionary<int, int> BuildIndex(Func<DiffLineTag, int> side)
	{
		var index = new Dictionary<int, int>();
		for (int i = 0; i < Tags.Count; i++)
		{
			int blobLine = side(Tags[i]);
			if (blobLine > 0)
				index[blobLine] = i + 1;
		}
		return index;
	}

	/// <summary>1-based document line for a 1-based new-file line, or null.</summary>
	public int? DocLineFromNewLine(int newLine)
		=> newToDoc.Value.TryGetValue(newLine, out int doc) ? doc : null;

	/// <summary>1-based document line for a 1-based old-file line, or null.</summary>
	public int? DocLineFromOldLine(int oldLine)
		=> oldToDoc.Value.TryGetValue(oldLine, out int doc) ? doc : null;
}

// DiffLib annotates its generic parameters as IList<T?>, which makes every call site a
// nullability mismatch for T=string even though our inputs never contain nulls. The
// builder interops with nullable warnings off instead of sprinkling suppressions.
#nullable disable warnings

/// <summary>
/// Builds the unified-diff document from the two blob texts using DiffLib alignment.
/// Changed runs are emitted GitHub-style (all removals, then all additions); aligned
/// replace pairs carry character-level word-diff spans on both sides.
/// </summary>
public static class DiffDocumentBuilder
{
	public static DiffDocumentModel Build(string oldText, string newText)
	{
		var oldLines = SplitLines(oldText);
		var newLines = SplitLines(newText);
		var sections = DiffLib.Diff.CalculateSections(oldLines, newLines, EqualityComparer<string>.Default);
		var aligned = DiffLib.Diff.AlignElements(
			oldLines, newLines, sections, new DiffLib.Alignment.StringSimilarityDiffElementAligner());

		var docLines = new List<string>();
		var tags = new List<DiffLineTag>();
		var pendingRemoved = new List<(string Text, DiffLineTag Tag)>();
		var pendingAdded = new List<(string Text, DiffLineTag Tag)>();
		int oldNo = 0, newNo = 0;

		void FlushRun()
		{
			foreach (var (text, tag) in pendingRemoved)
			{
				docLines.Add(text);
				tags.Add(tag);
			}
			foreach (var (text, tag) in pendingAdded)
			{
				docLines.Add(text);
				tags.Add(tag);
			}
			pendingRemoved.Clear();
			pendingAdded.Clear();
		}

		foreach (var element in aligned)
		{
			switch (element.Operation)
			{
				case DiffLib.DiffOperation.Match:
					FlushRun();
					oldNo++;
					newNo++;
					docLines.Add(element.ElementFromCollection2.Value);
					tags.Add(new DiffLineTag(DiffLineKind.Context, oldNo, newNo, null));
					break;
				case DiffLib.DiffOperation.Delete:
					oldNo++;
					pendingRemoved.Add((element.ElementFromCollection1.Value,
						new DiffLineTag(DiffLineKind.Removed, oldNo, 0, null)));
					break;
				case DiffLib.DiffOperation.Insert:
					newNo++;
					pendingAdded.Add((element.ElementFromCollection2.Value,
						new DiffLineTag(DiffLineKind.Added, 0, newNo, null)));
					break;
				case DiffLib.DiffOperation.Replace:
				case DiffLib.DiffOperation.Modify:
					oldNo++;
					newNo++;
					string oldLine = element.ElementFromCollection1.Value;
					string newLine = element.ElementFromCollection2.Value;
					var (oldSpans, newSpans) = ComputeWordDiffs(oldLine, newLine);
					pendingRemoved.Add((oldLine, new DiffLineTag(DiffLineKind.Removed, oldNo, 0, oldSpans)));
					pendingAdded.Add((newLine, new DiffLineTag(DiffLineKind.Added, 0, newNo, newSpans)));
					break;
				default:
					throw new InvalidOperationException($"Unexpected diff operation {element.Operation}");
			}
		}
		FlushRun();

		return new DiffDocumentModel {
			Text = string.Join("\n", docLines),
			Tags = tags,
			Hunks = ComputeHunks(tags),
		};
	}

	// git's line model: "a\n" is ONE line, so exactly one trailing newline is stripped
	// before splitting; a completely empty blob (added/deleted file sides) has NO lines.
	static string[] SplitLines(string text)
	{
		if (text.Length == 0)
			return [];
		text = text.ReplaceLineEndings("\n");
		if (text.EndsWith('\n'))
			text = text[..^1];
		return text.Split('\n');
	}

	static IReadOnlyList<HunkSpan> ComputeHunks(List<DiffLineTag> tags)
	{
		var hunks = new List<HunkSpan>();
		int runStart = 0; // 1-based; 0 = not in a run
		for (int i = 0; i < tags.Count; i++)
		{
			bool changed = tags[i].Kind != DiffLineKind.Context;
			if (changed && runStart == 0)
				runStart = i + 1;
			else if (!changed && runStart != 0)
			{
				hunks.Add(new HunkSpan(runStart, i));
				runStart = 0;
			}
		}
		if (runStart != 0)
			hunks.Add(new HunkSpan(runStart, tags.Count));
		return hunks;
	}

	static (IReadOnlyList<IntraLineSpan>? OldSpans, IReadOnlyList<IntraLineSpan>? NewSpans) ComputeWordDiffs(
		string oldLine, string newLine)
	{
		var sections = DiffLib.Diff.CalculateSections(
			oldLine.ToCharArray(), newLine.ToCharArray(), EqualityComparer<char>.Default);
		List<IntraLineSpan>? oldSpans = null, newSpans = null;
		int o = 0, n = 0;
		foreach (var section in sections)
		{
			if (!section.IsMatch)
			{
				if (section.LengthInCollection1 > 0)
					(oldSpans ??= []).Add(new IntraLineSpan(o, section.LengthInCollection1));
				if (section.LengthInCollection2 > 0)
					(newSpans ??= []).Add(new IntraLineSpan(n, section.LengthInCollection2));
			}
			o += section.LengthInCollection1;
			n += section.LengthInCollection2;
		}
		return (oldSpans, newSpans);
	}
}
