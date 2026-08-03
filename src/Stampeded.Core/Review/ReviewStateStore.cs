using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stampeded.Core.Review;

public sealed record StoredComment(Guid Id, CommentAnchor Anchor, string Body, DateTimeOffset CreatedAt);

sealed record ReviewStateFile(string HeadSha, Dictionary<string, bool> Viewed, List<StoredComment>? Drafts, Dictionary<string, bool>? GuideChecks, Dictionary<string, string>? Depth = null);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReviewStateFile))]
partial class ReviewStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Persists per-PR review progress (viewed files, draft comments) as JSON under the user
/// data directory. Viewed flags reset on a new head SHA; drafts are kept — their content
/// anchors re-attach across force-pushes.
/// </summary>
public sealed class ReviewStateStore
{
	static readonly JsonSerializerOptions JsonOptions = new() {
		WriteIndented = true,
		TypeInfoResolver = ReviewStateJsonContext.Default,
	};

	readonly string directory;
	string? currentPath;
	ReviewStateFile? current;

	/// <summary>When opening found state for an OLDER head: that head and its viewed flags.
	/// The caller can carry viewed over for files the new push did not touch (re-review:
	/// invalidate only what changed, do not repeat the whole first pass).</summary>
	public (string PreviousHead, Dictionary<string, bool> PreviousViewed)? Superseded { get; private set; }

	public ReviewStateStore(string? directory = null)
	{
		this.directory = directory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stampeded", "reviews");
	}

	public void Open(string repoKey, int prNumber, string headSha)
		=> OpenFile($"{Sanitize(repoKey)}_pr{prNumber}.json", headSha);

	/// <summary>State for a local base..head review (no PR); keyed by the range text.</summary>
	public void OpenLocal(string repoKey, string rangeKey, string headSha)
		=> OpenFile($"{Sanitize(repoKey)}_local_{Sanitize(rangeKey)}.json", headSha);

	void OpenFile(string fileName, string headSha)
	{
		currentPath = Path.Combine(directory, fileName);
		current = null;
		if (File.Exists(currentPath))
		{
			try
			{
				current = JsonSerializer.Deserialize<ReviewStateFile>(File.ReadAllText(currentPath), JsonOptions);
			}
			catch (JsonException)
			{
				// Corrupt state file: start fresh rather than failing the review.
			}
		}
		Superseded = null;
		if (current is null)
		{
			current = new ReviewStateFile(headSha, [], [], []);
		}
		else if (current.HeadSha != headSha)
		{
			Superseded = (current.HeadSha, new Dictionary<string, bool>(current.Viewed));
			current = current with { HeadSha = headSha, Viewed = [] };
			// Persist the head move now: reopening before the next SetViewed must not
			// re-read the old head from disk and report a second supersede.
			Save();
		}
	}

	public bool IsViewed(string path)
		=> current?.Viewed.GetValueOrDefault(path) ?? false;

	public void SetViewed(string path, bool viewed)
	{
		if (current is null)
			return;
		current.Viewed[path] = viewed;
		Save();
	}

	public bool GetGuideCheck(string stageId)
		=> current?.GuideChecks?.GetValueOrDefault(stageId) ?? false;

	public void SetGuideCheck(string stageId, bool value)
	{
		if (current is null)
			return;
		if (current.GuideChecks is null)
			current = current with { GuideChecks = [] };
		current.GuideChecks![stageId] = value;
		Save();
	}

	/// <summary>Planned review depth for a file: "deep", "skim", "trust" or "" (unset).
	/// Depth marks survive force-pushes - the plan outlives the head, unlike viewed flags.</summary>
	public string GetDepth(string path)
		=> current?.Depth?.GetValueOrDefault(path) ?? "";

	public void SetDepth(string path, string depth)
	{
		if (current is null)
			return;
		if (current.Depth is null)
			current = current with { Depth = [] };
		current.Depth![path] = depth;
		Save();
	}

	public IReadOnlyList<StoredComment> Drafts => current?.Drafts ?? [];

	public void AddDraft(StoredComment draft)
	{
		if (current is null)
			return;
		if (current.Drafts is null)
			current = current with { Drafts = [] };
		current.Drafts!.Add(draft);
		Save();
	}

	public void RemoveDraft(Guid id)
	{
		if (current?.Drafts is null)
			return;
		current.Drafts.RemoveAll(d => d.Id == id);
		Save();
	}

	void Save()
	{
		if (current is null || currentPath is null)
			return;
		Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
		File.WriteAllText(currentPath, JsonSerializer.Serialize(current, JsonOptions));
	}

	static string Sanitize(string key)
	{
		var invalid = Path.GetInvalidFileNameChars();
		return new string(key.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
	}
}
