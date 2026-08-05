namespace Stampeded.Core.Diff;

public enum DiffLineKind
{
	Context,
	Added,
	Removed,
	Filler,
	/// <summary>A synthetic line reserved for an inline comment thread; maps to no blob line.</summary>
	Comment,
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

/// <summary>The two aligned documents of a side-by-side diff (equal line counts).</summary>
public sealed record SideBySideModel(
	string LeftText,
	string RightText,
	IReadOnlyList<DiffLineTag> LeftTags,
	IReadOnlyList<DiffLineTag> RightTags);

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

	/// <summary>One side's text reconstructed from the document (the document itself is
	/// not valid source - removed lines interleave), plus the 1-based document line for
	/// each side line, for mapping parse results back.</summary>
	public (string Text, IReadOnlyList<int> SideToDocLine) GetSideText(bool oldSide)
	{
		var docLines = Text.Split('\n');
		var sideLines = new List<string>();
		var sideToDoc = new List<int>();
		for (int i = 0; i < Tags.Count && i < docLines.Length; i++)
		{
			int sideLine = oldSide ? Tags[i].OldLine : Tags[i].NewLine;
			if (sideLine > 0)
			{
				sideLines.Add(docLines[i]);
				sideToDoc.Add(i + 1);
			}
		}
		return (string.Join('\n', sideLines), sideToDoc);
	}

	public const string ThreadMarkerPrefix = "@@thread:";
	public const string ThreadMarkerSuffix = "@@";

	/// <summary>Derives a model with one synthetic marker line inserted below each
	/// anchor's document line. The marker text carries the anchor key; the view replaces
	/// it with an interactive thread control. A pure splice - the diff is not recomputed,
	/// blob mappings shift with the insertions, and hunk spans stretch over insertions
	/// inside them.</summary>
	public DiffDocumentModel WithThreadLines(IReadOnlyList<ThreadAnchor> anchors)
	{
		var insertAfter = new SortedDictionary<int, List<string>>();
		foreach (var anchor in anchors)
		{
			// BlobLine 0 = no surviving location (outdated): pinned before the first line.
			int? docLine = anchor.BlobLine == 0 ? 0
				: anchor.OldSide ? DocLineFromOldLine(anchor.BlobLine) : DocLineFromNewLine(anchor.BlobLine);
			if (docLine is not { } dl)
				continue;
			if (!insertAfter.TryGetValue(dl, out var keys))
				insertAfter[dl] = keys = [];
			keys.Add(anchor.Key);
		}
		if (insertAfter.Count == 0)
			return this;

		var sourceLines = Text.Split('\n');
		var newLines = new List<string>(sourceLines.Length + anchors.Count);
		var newTags = new List<DiffLineTag>(Tags.Count + anchors.Count);
		// 1-based doc line -> number of lines inserted at or before it, for hunk shifting.
		var shiftAt = new int[Tags.Count + 2];
		if (insertAfter.TryGetValue(0, out var topKeys))
		{
			foreach (var key in topKeys)
			{
				newLines.Add(ThreadMarkerPrefix + key + ThreadMarkerSuffix);
				newTags.Add(new DiffLineTag(DiffLineKind.Comment, 0, 0, null));
			}
			shiftAt[0] = topKeys.Count;
		}
		for (int i = 0; i < Tags.Count; i++)
		{
			newLines.Add(sourceLines[i]);
			newTags.Add(Tags[i]);
			if (insertAfter.TryGetValue(i + 1, out var keys))
			{
				foreach (var key in keys)
				{
					newLines.Add(ThreadMarkerPrefix + key + ThreadMarkerSuffix);
					newTags.Add(new DiffLineTag(DiffLineKind.Comment, 0, 0, null));
				}
				shiftAt[i + 1] = keys.Count;
			}
		}
		// Keep any trailing text beyond the tagged lines (e.g. final newline artifacts).
		for (int i = Tags.Count; i < sourceLines.Length; i++)
			newLines.Add(sourceLines[i]);
		var cumulative = new int[shiftAt.Length];
		cumulative[0] = shiftAt[0];
		for (int i = 1; i < shiftAt.Length; i++)
			cumulative[i] = cumulative[i - 1] + shiftAt[i];
		int Shift(int docLine) => docLine + cumulative[Math.Min(docLine, cumulative.Length - 1)];
		// An insertion sits BELOW its anchor line, so a hunk ending exactly at the anchor
		// stretches over the thread; a hunk starting after it just moves down.
		var newHunks = Hunks
			.Select(h => new HunkSpan(
				h.FirstDocLine + cumulative[Math.Min(h.FirstDocLine - 1, cumulative.Length - 1)],
				Shift(h.LastDocLine)))
			.ToList();
		return new DiffDocumentModel {
			Text = string.Join('\n', newLines),
			Tags = newTags,
			Hunks = newHunks,
		};
	}
}

/// <summary>Where a comment thread attaches: a blob line on one side, plus the key the
/// view uses to find the thread content for the marker line.</summary>
public sealed record ThreadAnchor(bool OldSide, int BlobLine, string Key);

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

	/// <summary>
	/// Builds the two aligned documents of a side-by-side view from one alignment: equal
	/// line counts, with empty Filler rows where the other side inserted or deleted.
	/// </summary>
	public static SideBySideModel BuildPair(string oldText, string newText)
	{
		var oldLines = SplitLines(oldText);
		var newLines = SplitLines(newText);
		var sections = DiffLib.Diff.CalculateSections(oldLines, newLines, EqualityComparer<string>.Default);
		var aligned = DiffLib.Diff.AlignElements(
			oldLines, newLines, sections, new DiffLib.Alignment.StringSimilarityDiffElementAligner());

		var left = new List<string>();
		var right = new List<string>();
		var leftTags = new List<DiffLineTag>();
		var rightTags = new List<DiffLineTag>();
		int oldNo = 0, newNo = 0;
		var filler = new DiffLineTag(DiffLineKind.Filler, 0, 0, null);

		foreach (var element in aligned)
		{
			switch (element.Operation)
			{
				case DiffLib.DiffOperation.Match:
					oldNo++;
					newNo++;
					left.Add(element.ElementFromCollection1.Value);
					right.Add(element.ElementFromCollection2.Value);
					leftTags.Add(new DiffLineTag(DiffLineKind.Context, oldNo, newNo, null));
					rightTags.Add(new DiffLineTag(DiffLineKind.Context, oldNo, newNo, null));
					break;
				case DiffLib.DiffOperation.Delete:
					oldNo++;
					left.Add(element.ElementFromCollection1.Value);
					right.Add("");
					leftTags.Add(new DiffLineTag(DiffLineKind.Removed, oldNo, 0, null));
					rightTags.Add(filler);
					break;
				case DiffLib.DiffOperation.Insert:
					newNo++;
					left.Add("");
					right.Add(element.ElementFromCollection2.Value);
					leftTags.Add(filler);
					rightTags.Add(new DiffLineTag(DiffLineKind.Added, 0, newNo, null));
					break;
				case DiffLib.DiffOperation.Replace:
				case DiffLib.DiffOperation.Modify:
					oldNo++;
					newNo++;
					string oldLine = element.ElementFromCollection1.Value;
					string newLine = element.ElementFromCollection2.Value;
					var (oldSpans, newSpans) = ComputeWordDiffs(oldLine, newLine);
					left.Add(oldLine);
					right.Add(newLine);
					leftTags.Add(new DiffLineTag(DiffLineKind.Removed, oldNo, 0, oldSpans));
					rightTags.Add(new DiffLineTag(DiffLineKind.Added, 0, newNo, newSpans));
					break;
				default:
					throw new InvalidOperationException($"Unexpected diff operation {element.Operation}");
			}
		}
		return new SideBySideModel(
			string.Join("\n", left), string.Join("\n", right), leftTags, rightTags);
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
