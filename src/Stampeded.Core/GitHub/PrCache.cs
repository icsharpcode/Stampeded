using System.Text.Json;
using System.Text.Json.Serialization;

using Stampeded.Core.Infra;

namespace Stampeded.Core.GitHub;

/// <summary>
/// What GitHub said about a pull request the last time it could be reached: enough to open the
/// review again without it.
///
/// The change itself is never in here. Its commits are in the object database from the fetch
/// that first opened the review, and the diff is read from them - what GitHub alone knows is
/// the pull request's own description, the comments people left on it and the state of its
/// checks, and that is what this keeps.
/// </summary>
public sealed record PrSnapshot(
	PrDetail Detail,
	string HeadSha,
	string BaseSha,
	DateTimeOffset TakenAt,
	IReadOnlyList<PostedComment>? Comments = null,
	IReadOnlyList<CheckRun>? Checks = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PrSnapshot))]
partial class PrCacheJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The snapshots, one file per pull request under the user's cache directory. A cache, not
/// state: deleting it costs a reader nothing but the ability to open that review offline.
/// </summary>
public static class PrCache
{
	static readonly JsonSerializerOptions Options = new() {
		WriteIndented = true,
		TypeInfoResolver = PrCacheJsonContext.Default,
	};

	static string PathFor(string repoKey, int number) => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".cache", "stampeded", "prs", $"{Sanitize(repoKey)}_pr{number}.json");

	public static PrSnapshot? Load(string repoKey, int number)
	{
		string path = PathFor(repoKey, number);
		try
		{
			return File.Exists(path)
				? JsonSerializer.Deserialize<PrSnapshot>(File.ReadAllText(path), Options)
				: null;
		}
		catch (Exception ex) when (ex is IOException or JsonException)
		{
			// An unreadable cache is a cache miss, which is a working state.
			CliLog.Write("cache", $"unreadable snapshot for #{number}: {ex.Message}");
			return null;
		}
	}

	public static void Save(string repoKey, PrSnapshot snapshot)
	{
		string path = PathFor(repoKey, snapshot.Detail.Number);
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, JsonSerializer.Serialize(snapshot, Options));
		}
		catch (IOException ex)
		{
			CliLog.Write("cache", $"could not write snapshot for #{snapshot.Detail.Number}: {ex.Message}");
		}
	}

	static string Sanitize(string key) => string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}
