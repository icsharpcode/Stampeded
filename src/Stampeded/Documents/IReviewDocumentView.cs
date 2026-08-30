namespace Stampeded.Documents;

/// <summary>What a document view can be asked to do. A view answers with the ones it has, and
/// the menu greys out the rest rather than offering a command that would do nothing.</summary>
[Flags]
public enum ReviewCommands
{
	None = 0,
	JumpToHunk = 1 << 0,
	JumpToUncovered = 1 << 1,
	ToggleBlame = 1 << 2,
	CommentAtCaret = 1 << 3,
	GoToDefinition = 1 << 4,
	FindReferences = 1 << 5,
	HighlightOccurrences = 1 << 6,
	ShowCallGraph = 1 << 7,
	HistoryOfSelection = 1 << 8,
	DebugHere = 1 << 9,
}

/// <summary>
/// The commands a diff document answers to, whichever way it lays the change out.
///
/// This exists so the two layouts cannot drift apart quietly. Every command the window offers
/// is a member here, so a view that does not have one has to say so in code that will not
/// compile until it does - where before, the window reached for a static "active view" that
/// only the unified layout ever set, and a command pressed over a side-by-side tab acted on
/// whatever unified document happened to be behind it.
/// </summary>
public interface IReviewDocumentView
{
	/// <summary>Which of these commands this view actually carries out.</summary>
	ReviewCommands Supported { get; }

	/// <summary>The document this view is showing, so the window can tell whether it is the
	/// one in front.</summary>
	string DocumentId { get; }

	/// <summary>Where the caret is, as a line of the blob this view shows and which side that
	/// line belongs to. Null on a row that belongs to neither blob, and null in a view with no
	/// caret at all - navigation history records what it can and line 1 otherwise.</summary>
	(int BlobLine, bool OldSide)? CaretOrigin { get; }

	/// <summary>Moves to the next or previous hunk, and says whether there was one: past the
	/// last, reading carries on in the next file rather than stopping.</summary>
	bool JumpToHunkCommand(int direction);

	/// <summary>Lands on the first hunk of this file when stepping forwards, or the last when
	/// stepping back - where a reader arriving from the file before or after expects to be.
	/// </summary>
	void JumpToEdgeHunk(int direction);

	void JumpToUncoveredCommand();

	bool BlameVisible { get; }

	void ToggleBlameCommand();

	void CommentAtCaretCommand();

	void GoToDefinitionCommand();

	void FindReferencesCommand();

	void HighlightOccurrencesCommand();

	void ShowCallGraphCommand();

	void HistoryOfSelectionCommand();

	void DebugHereCommand();
}

/// <summary>
/// Which view is showing which document, so a command reaches the document in front of the
/// reader. Keyed by dockable id and asked against the active dockable rather than tracking
/// focus: clicking a tab header need not move focus into the view, and a command that lands on
/// a document nobody is looking at is worse than one that does nothing.
/// </summary>
public static class ReviewViews
{
	static readonly Dictionary<string, IReviewDocumentView> byDocument = [];

	/// <summary>Records which document this view shows. Called again whenever that changes -
	/// a docked view outlives the document it was given - so any id it held before is dropped
	/// first.</summary>
	public static void Register(IReviewDocumentView view)
	{
		Unregister(view);
		if (view.DocumentId is { Length: > 0 } id)
			byDocument[id] = view;
	}

	public static void Unregister(IReviewDocumentView view)
	{
		foreach (var (id, registered) in byDocument.ToList())
		{
			if (ReferenceEquals(registered, view))
				byDocument.Remove(id);
		}
	}

	/// <summary>The view of the document in front, if that document is a diff.</summary>
	public static IReviewDocumentView? Active
		=> App.Workspace?.Documents?.ActiveDockable?.Id is { Length: > 0 } id
			? byDocument.GetValueOrDefault(id)
			: null;
}
