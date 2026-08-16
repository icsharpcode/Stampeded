using System.Collections.ObjectModel;

using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Documents;

/// <summary>One comment with the lines it was written about.</summary>
public sealed record ReviewCommentRow(
	string RelPath, int? Line, bool OldSide, string Author, string Body, string Context, bool IsDraft)
{
	public string Header =>
		$"{RelPath}:{(Line?.ToString() ?? "outdated")}{(OldSide ? " (base)" : "")}";

	public string Who => IsDraft ? "draft (not posted yet)" : Author;
}

public sealed partial class ReviewDocumentState : ObservableObject
{
	[ObservableProperty]
	string status = "";

	[ObservableProperty]
	string summary = "";

	[ObservableProperty]
	bool canGiveVerdict;

	[ObservableProperty]
	bool canComment;

	[ObservableProperty]
	bool isEmpty = true;

	/// <summary>False unless a pull request is open and GitHub says it would take a merge.</summary>
	[ObservableProperty]
	bool canMerge;

	/// <summary>GitHub's own words for the merge state, shown whether or not it allows one:
	/// a disabled button that says nothing leaves the reader guessing at a repository
	/// setting, a branch that is behind, or missing push access.</summary>
	[ObservableProperty]
	string mergeState = "";

	/// <summary>The same state spelled out - what refuses the merge and what would settle it -
	/// for the tooltip. Two words are a name for the refusal, not an account of it.</summary>
	[ObservableProperty]
	string mergeExplanation = "";

	/// <summary>The button says what pressing it would do, and then why it can or cannot be
	/// pressed: one hover, both halves of the question.</summary>
	public string MergeButtonTip => "Merge this pull request on GitHub, for everyone. Asks first.\n\n"
		+ MergeExplanation;

	partial void OnMergeExplanationChanged(string value) => OnPropertyChanged(nameof(MergeButtonTip));

	/// <summary>The merge methods this repository allows, as gh flag names.</summary>
	public ObservableCollection<string> MergeMethods { get; } = [];

	[ObservableProperty]
	string selectedMergeMethod = "";

	/// <summary>What the last submission did, kept apart from <see cref="Status"/>: posting a
	/// review reloads the comments, and the reload used to overwrite the answer with the same
	/// sentence the page shows at rest - so a review that went out looked like a button that
	/// did nothing.</summary>
	[ObservableProperty]
	string outcome = "";

	/// <summary>Behind the summary box and the verdict buttons: green when the review went
	/// out, red when it did not, and nothing at all before either.</summary>
	[ObservableProperty]
	IBrush outcomeBackground = Brushes.Transparent;

	[ObservableProperty]
	bool hasOutcome;

	/// <summary>Set once the list has been filled and a starting choice made: what is worth
	/// remembering is a choice the reader made, not the one this repository's allowed methods
	/// left over.</summary>
	public bool RememberMergeMethod { get; set; }

	partial void OnSelectedMergeMethodChanged(string value)
	{
		if (RememberMergeMethod && value.Length > 0)
			MergeMethodPreference.Save(value);
	}
}

/// <summary>
/// Every comment of this review in one place, each under the lines it is about, with the
/// verdict at the bottom - the page a reader reaches for when deciding, rather than a list of
/// one-line previews in a pane that shows the code only after a jump.
/// </summary>
public sealed class ReviewDocumentViewModel : Document
{
	readonly ReviewWorkspace workspace;

	/// <summary>Blob text per revision and path: several comments usually land in the same
	/// file, and each one only needs a handful of lines out of it.</summary>
	readonly Dictionary<(string Rev, string Path), string[]> blobs = new();

	public ObservableCollection<ReviewCommentRow> Comments { get; } = [];
	public ReviewDocumentState State { get; } = new();

	/// <summary>Lines of code shown on each side of the commented line.</summary>
	const int ContextLines = 3;

	public ReviewDocumentViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.Comments.Changed += Rebuild;
		Rebuild();
	}

	void Rebuild()
	{
		// Two jobs, started apart on purpose: quoting every comment reads a blob per file, and
		// a file that cannot be read there must not be what leaves the verdict buttons dead.
		RefreshVerdictAsync().HandleExceptions();
		RebuildAsync().HandleExceptions();
	}

	async Task RefreshVerdictAsync()
	{
		State.CanComment = workspace.Comments.CanComment;
		State.CanGiveVerdict = workspace.Comments.CanComment && !await workspace.Comments.IsOwnPullRequestAsync();
	}

	async Task RebuildAsync()
	{
		Comments.Clear();
		foreach (var draft in workspace.Comments.Drafts)
		{
			Comments.Add(new ReviewCommentRow(
				draft.Stored.Anchor.Path, draft.CurrentLine, draft.Stored.Anchor.OldSide, "",
				draft.Stored.Body,
				await ContextAsync(draft.Stored.Anchor.Path, draft.CurrentLine, draft.Stored.Anchor.OldSide),
				IsDraft: true));
		}
		foreach (var posted in workspace.Comments.Posted)
		{
			Comments.Add(new ReviewCommentRow(
				posted.RelPath, posted.Line, posted.OldSide,
				posted.IsResolved ? $"{posted.Author} (resolved)" : posted.Author,
				posted.Body,
				await ContextAsync(posted.RelPath, posted.Line, posted.OldSide),
				IsDraft: false));
		}
		State.IsEmpty = Comments.Count == 0;
		State.CanComment = workspace.Comments.CanComment;
		int drafts = workspace.Comments.Drafts.Count;
		State.Status = workspace.Comments.CanComment
			? $"{drafts} draft(s) will be posted with this review; {workspace.Comments.Posted.Count} comment(s) already on the pull request."
			: "Local review: comments need a pull request to post to.";
		await RefreshMergeAsync();
	}

	/// <summary>
	/// What GitHub says about merging, read fresh: it changes with every push to either branch
	/// and with every review someone else leaves, so a state read when the review opened would
	/// be stale exactly when it matters.
	/// </summary>
	async Task RefreshMergeAsync()
	{
		if (workspace.CurrentPr is not { } pr)
		{
			State.CanMerge = false;
			State.MergeState = "";
			State.MergeExplanation = "";
			return;
		}
		try
		{
			if (State.MergeMethods.Count == 0)
			{
				foreach (var method in (await workspace.GitHub.GetMergeMethodsAsync()).Allowed)
					State.MergeMethods.Add(method);
				// Whatever was chosen last, if this repository allows it; a merge commit
				// otherwise, because the series a review was read as is worth keeping.
				string remembered = MergeMethodPreference.Load();
				State.SelectedMergeMethod = State.MergeMethods.FirstOrDefault(m => m == remembered)
					?? State.MergeMethods.FirstOrDefault(m => m == MergeMethodPreference.Default)
					?? State.MergeMethods.FirstOrDefault() ?? "";
				State.RememberMergeMethod = true;
			}
			var merge = await workspace.GitHub.GetMergeStateAsync(pr.Number);
			State.MergeState = $"merge: {merge.Describe}";
			State.MergeExplanation = merge.Explain
				+ (State.MergeMethods.Count == 0
					? "\n- This repository allows no merge method through the API."
					: "");
			State.CanMerge = merge.CanMerge && State.MergeMethods.Count > 0;
		}
		catch (ToolFailedException ex)
		{
			State.CanMerge = false;
			State.MergeState = $"merge state unknown ({ex.Message})";
			State.MergeExplanation = $"Asking GitHub about the merge state failed:\n{ex.Message}";
		}
	}

	public void Merge()
	{
		MergeAsync().HandleExceptions();

		async Task MergeAsync()
		{
			if (State.SelectedMergeMethod is not { Length: > 0 } method)
				return;
			State.Status = await workspace.MergeCurrentPrAsync(method);
			await RefreshMergeAsync();
		}
	}

	/// <summary>
	/// The commented line with a few above and below it, each numbered, the commented one
	/// marked. Empty when there is no line to show it at - an outdated draft has lost the
	/// place it was written about, which is the honest thing to say about it.
	/// </summary>
	async Task<string> ContextAsync(string path, int? line, bool oldSide)
	{
		if (line is not { } target)
			return "";
		string? rev = oldSide ? workspace.BaseSha : workspace.HeadSha;
		if (rev is null)
			return "";
		if (!blobs.TryGetValue((rev, path), out var lines))
		{
			try
			{
				lines = (await workspace.Git.ShowFileAsync(rev, path)).ReplaceLineEndings("\n").Split('\n');
			}
			catch (ToolFailedException)
			{
				// Added on the other side, or renamed: no blob to quote from at this revision.
				lines = [];
			}
			blobs[(rev, path)] = lines;
		}
		if (lines.Length == 0)
			return "";
		int first = Math.Max(1, target - ContextLines);
		int last = Math.Min(lines.Length, target + ContextLines);
		var text = new System.Text.StringBuilder();
		for (int i = first; i <= last; i++)
			text.AppendLine($"{(i == target ? ">" : " ")} {i,5}  {lines[i - 1]}");
		return text.ToString().TrimEnd('\n');
	}

	public void Open(ReviewCommentRow row)
		=> workspace.NavigateToFileLineAsync(row.RelPath, row.Line ?? 1, row.OldSide && row.Line is not null, record: true)
			.HandleExceptions();

	public void Submit(string eventType)
	{
		SubmitAsync().HandleExceptions();

		async Task SubmitAsync()
		{
			State.HasOutcome = false;
			State.Outcome = $"Submitting {eventType}...";
			State.OutcomeBackground = Brushes.Transparent;
			string result = await workspace.Comments.SubmitCheckedAsync(eventType, State.Summary.Trim());
			bool posted = result.StartsWith("Review submitted", StringComparison.Ordinal);
			if (posted)
				State.Summary = "";
			State.Outcome = result;
			State.HasOutcome = true;
			// Faint, because it sits behind text that has to stay readable in either theme,
			// and it is the answer to one press rather than a state of the review.
			State.OutcomeBackground = new SolidColorBrush(Color.Parse(posted ? "#2EA043" : "#F85149"), 0.16);
			workspace.PostStatus(result);
		}
	}

	public void Refresh() => RefreshAsync().HandleExceptions();

	async Task RefreshAsync()
	{
		State.Status = "Refreshing posted comments...";
		try
		{
			await workspace.Comments.RefreshPostedAsync();
		}
		catch (ToolFailedException ex)
		{
			// Rebuild would otherwise overwrite this with a count that did not change.
			State.Status = $"Refresh failed: {ex.Message}";
		}
	}
}
