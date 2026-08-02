using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stampeded.Core.Review;

sealed record ReviewStateFile(string HeadSha, Dictionary<string, bool> Viewed);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ReviewStateFile))]
partial class ReviewStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Persists per-PR review progress (viewed files) as JSON under the user data directory.
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

	public ReviewStateStore(string? directory = null)
	{
		this.directory = directory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stampeded", "reviews");
	}

	/// <summary>
	/// Loads viewed-state for a review. State from an older head SHA is discarded wholesale.
	/// ponytail: per-file invalidation by diff-content hash would preserve more across
	/// force-pushes; add when whole-PR reset proves annoying.
	/// </summary>
	public void Open(string repoKey, int prNumber, string headSha)
	{
		currentPath = Path.Combine(directory, $"{Sanitize(repoKey)}_pr{prNumber}.json");
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
		if (current is null || current.HeadSha != headSha)
			current = new ReviewStateFile(headSha, new Dictionary<string, bool>());
	}

	public bool IsViewed(string path)
		=> current?.Viewed.GetValueOrDefault(path) ?? false;

	public void SetViewed(string path, bool viewed)
	{
		if (current is null || currentPath is null)
			return;
		current.Viewed[path] = viewed;
		Directory.CreateDirectory(Path.GetDirectoryName(currentPath)!);
		File.WriteAllText(currentPath, JsonSerializer.Serialize(current, JsonOptions));
	}

	static string Sanitize(string key)
	{
		var invalid = Path.GetInvalidFileNameChars();
		return new string(key.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
	}
}
