using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stampeded.Core.MergeQueue;

/// <summary>One pull request cleared to land, in the order it was cleared.</summary>
/// <param name="Pr">The pull request number.</param>
/// <param name="Title">Its title when it was queued, so the list reads without asking GitHub.</param>
/// <param name="HeadSha">The revision that was queued. A push to the branch after this makes the
/// entry stale: what a reviewer cleared was this revision and not whatever replaced it.</param>
/// <param name="Method">A gh merge flag name: merge, squash or rebase. The enqueuer's choice,
/// carried along so whichever client drains the queue merges the way they meant.</param>
public sealed record MergeQueueEntry(
	int Pr,
	string Title,
	string HeadSha,
	string Method,
	string By,
	DateTimeOffset At);

/// <summary>
/// The client currently merging. <paramref name="Holder"/> is who to name in the UI;
/// <paramref name="Client"/> is a per-process id, so two Stampeded windows on one machine can
/// still tell whose lock this is.
/// </summary>
public sealed record MergeQueueLock(
	string Holder,
	string Client,
	DateTimeOffset At,
	int Pr)
{
	public bool IsExpired(TimeSpan lease) => DateTimeOffset.UtcNow - At > lease;
}

/// <summary>
/// The whole shared queue. Serialized into the message of a commit on the queue ref, so the
/// remote holds one document and git's fast-forward rule decides who gets to replace it.
/// </summary>
public sealed record MergeQueueDocument(
	int Version,
	IReadOnlyList<MergeQueueEntry> Entries,
	MergeQueueLock? Lock)
{
	public const int CurrentVersion = 1;

	public static readonly MergeQueueDocument Empty = new(CurrentVersion, [], null);

	public MergeQueueEntry? Find(int pr) => Entries.FirstOrDefault(e => e.Pr == pr);

	public MergeQueueDocument With(IReadOnlyList<MergeQueueEntry> entries) => this with { Entries = entries };
}

[JsonSourceGenerationOptions(
	WriteIndented = true,
	PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
	DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MergeQueueDocument))]
partial class MergeQueueJsonContext : JsonSerializerContext;

static class MergeQueueJson
{
	public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		TypeInfoResolver = MergeQueueJsonContext.Default,
	};
}
