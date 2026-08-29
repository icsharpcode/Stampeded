using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stampeded.Core.Review;

/// <summary>A drafted comment. <paramref name="InReplyTo"/> is the REST id of the posted
/// comment it answers, which makes it a reply into that thread instead of a new one on the
/// same line; null for a remark of its own.</summary>
public sealed record StoredComment(Guid Id, CommentAnchor Anchor, string Body, DateTimeOffset CreatedAt,
	long? InReplyTo = null);

// Depth carried a per-file review plan that no longer exists. The field stays so state files
// written by an older build still parse; nothing reads or writes it.
sealed record ReviewStateFile(string HeadSha, Dictionary<string, bool> Viewed, List<StoredComment>? Drafts, Dictionary<string, bool>? GuideChecks, Dictionary<string, string>? Depth = null, string? BaseSha = null, string? PreviousHead = null, string? PreviousBase = null,
	string? MarkedHead = null, string? MarkedBase = null, string? SubmittedHead = null, string? SubmittedBase = null,
	string? PreviousMarkedHead = null, string? PreviousMarkedBase = null,
	string? PreviousSubmittedHead = null, string? PreviousSubmittedBase = null);

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

	/// <summary>The head this review was last read at before the current one, or null on a
	/// first pass. Unlike <see cref="Superseded"/> this outlives the open that discovered the
	/// move: a reader who closes the app and comes back still wants the diff against what
	/// they read last time, not a fresh pass over the whole change.</summary>
	public string? PreviousHead => current?.PreviousHead;

	/// <summary>The base that head was reviewed against, so the work of that pass can be
	/// identified as base..head however the branch was rewritten since.</summary>
	public string? PreviousBase => current?.PreviousBase;

	/// <summary>The head at which the reader last ticked a file off, before the current one -
	/// the last head they can be said to have read rather than merely opened. Null until they
	/// have ticked one off at a head that has since moved.</summary>
	public string? PreviousMarkedHead => current?.PreviousMarkedHead;

	public string? PreviousMarkedBase => current?.PreviousMarkedBase;

	/// <summary>The head at which the reader last submitted a review, before the current one:
	/// the point they last said something to the author about.</summary>
	public string? PreviousSubmittedHead => current?.PreviousSubmittedHead;

	public string? PreviousSubmittedBase => current?.PreviousSubmittedBase;

	public ReviewStateStore(string? directory = null)
	{
		this.directory = directory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "stampeded", "reviews");
	}

	public void Open(string repoKey, int prNumber, string headSha, string? baseSha = null)
		=> OpenFile($"{Sanitize(repoKey)}_pr{prNumber}.json", headSha, baseSha);

	/// <summary>State for reading one commit on its own. Keyed by the commit, so having
	/// read a file in one commit says nothing about the next commit's change to it.</summary>
	public void OpenCommitScope(string repoKey, string commitSha)
		=> OpenFile($"{Sanitize(repoKey)}_commit_{commitSha[..9]}.json", commitSha, null);

	/// <summary>State for reading the uncommitted work on its own. Keyed by the commit it sits
	/// on rather than by content: what is in the checkout changes with every save, and a file
	/// read there has been read for the tip it was written against.</summary>
	public void OpenWorkingTreeScope(string repoKey, string tipSha)
		=> OpenFile($"{Sanitize(repoKey)}_worktree_{tipSha[..9]}.json", tipSha, null);

	/// <summary>State for a local base..head review (no PR); keyed by the range text.</summary>
	public void OpenLocal(string repoKey, string rangeKey, string headSha, string? baseSha = null)
		=> OpenFile($"{Sanitize(repoKey)}_local_{Sanitize(rangeKey)}.json", headSha, baseSha);

	void OpenFile(string fileName, string headSha, string? baseSha)
	{
		currentPath = Path.Combine(directory, fileName);
		current = null;
		if (File.Exists(currentPath))
		{
			try
			{
				current = JsonSerializer.Deserialize<ReviewStateFile>(File.ReadAllText(currentPath), JsonOptions);
			}
			catch (JsonException ex)
			{
				// Corrupt state file: start fresh rather than failing the review - but say so,
				// because everything the reader recorded here is about to look like a first
				// pass that never happened.
				Infra.CliLog.Write("review", $"unreadable review state {Path.GetFileName(currentPath)}: {ex.Message}");
			}
		}
		Superseded = null;
		if (current is null)
		{
			// Written straight away, before anything has been read: what the next pass needs
			// from this one is the head it was opened at, and a reader who looks around and
			// comes back tomorrow has still had a pass at this head.
			current = new ReviewStateFile(headSha, [], [], []) { BaseSha = baseSha };
			Save();
		}
		else if (current.HeadSha != headSha)
		{
			Superseded = (current.HeadSha, new Dictionary<string, bool>(current.Viewed));
			// The outgoing pass becomes the baseline the next one is read against. Recorded
			// only here: writing it whenever the review is opened would collapse the baseline
			// onto the head being read, and there would be nothing left to compare with.
			current = current with {
				HeadSha = headSha,
				Viewed = [],
				BaseSha = baseSha,
				PreviousHead = current.HeadSha,
				PreviousBase = current.BaseSha,
				// What the outgoing pass was: where a file was ticked off, where a review was
				// submitted. A pass that did neither leaves the older marks standing, which is
				// what keeps opening a review from counting as having read it.
				PreviousMarkedHead = current.MarkedHead ?? current.PreviousMarkedHead,
				PreviousMarkedBase = current.MarkedHead is not null ? current.MarkedBase : current.PreviousMarkedBase,
				PreviousSubmittedHead = current.SubmittedHead ?? current.PreviousSubmittedHead,
				PreviousSubmittedBase = current.SubmittedHead is not null
					? current.SubmittedBase
					: current.PreviousSubmittedBase,
				MarkedHead = null,
				MarkedBase = null,
				SubmittedHead = null,
				SubmittedBase = null,
			};
			// Persist the head move now: reopening before the next SetViewed must not
			// re-read the old head from disk and report a second supersede.
			Save();
		}
		else if (baseSha is not null && current.BaseSha != baseSha)
		{
			// The head stayed put while the target branch moved under it, so the same work
			// is now read against a different base.
			current = current with { BaseSha = baseSha };
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
		// Ticking a file off is the act that makes this head a pass. Unticking is not: it says
		// the reader wants to look again, not that they never did.
		if (viewed)
			current = current with { MarkedHead = current.HeadSha, MarkedBase = current.BaseSha };
		Save();
	}

	/// <summary>Records that a review was submitted at the head being read, so a later pass can
	/// be measured from what the author was last told.</summary>
	public void RecordReviewSubmitted()
	{
		if (current is null)
			return;
		current = current with { SubmittedHead = current.HeadSha, SubmittedBase = current.BaseSha };
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

	/// <summary>Rewrites a draft's text. Its anchor and the time it was written stay: it is
	/// the same remark, said better.</summary>
	public void UpdateDraft(Guid id, string body)
	{
		if (current?.Drafts is null)
			return;
		int index = current.Drafts.FindIndex(d => d.Id == id);
		if (index < 0)
			return;
		current.Drafts[index] = current.Drafts[index] with { Body = body };
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
		// Written beside the file and moved into place: this is rewritten in full on every
		// toggled flag, and a write interrupted half way leaves JSON that reads as a review
		// nobody ever started - drafts, depth marks and the previous head with it.
		string temporary = currentPath + ".tmp";
		File.WriteAllText(temporary, JsonSerializer.Serialize(current, JsonOptions));
		File.Move(temporary, currentPath, overwrite: true);
	}

	static string Sanitize(string key)
	{
		var invalid = Path.GetInvalidFileNameChars();
		return new string(key.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
	}
}
