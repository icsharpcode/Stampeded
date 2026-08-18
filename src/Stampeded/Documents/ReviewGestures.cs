using Avalonia.Input;

namespace Stampeded.Documents;

/// <summary>
/// The single-key gestures a review is read with, in one place for both layouts.
///
/// They are handled on the way down to the editor, not on the way back up: AvaloniaEdit has
/// bindings of its own for some of them - Ctrl+Down and Ctrl+Up scroll by a line - and a diff
/// nobody types into has more use for the review's meaning than for the editor's. Reaching the
/// window as a fallback is not enough, because a key the editor has already acted on never
/// gets there.
///
/// A gesture that this layout has not got is answered by the view, which says so; that is the
/// difference between the layouts and it is written down in one place.
/// </summary>
static class ReviewGestures
{
	/// <summary>Acts on a key press, and says whether it was one of these gestures.</summary>
	public static bool Handle(KeyEventArgs e, IReviewDocumentView view)
	{
		var workspace = App.Workspace;
		switch (e.Key, e.KeyModifiers)
		{
			// n/p are the review's own keys; Ctrl+Down/Up are the ones a hand arrives with.
			case (Key.N, KeyModifiers.None):
			case (Key.Down, KeyModifiers.Control):
				StepHunk(view, 1);
				return true;
			case (Key.P, KeyModifiers.None):
			case (Key.Up, KeyModifiers.Control):
				StepHunk(view, -1);
				return true;
			case (Key.OemCloseBrackets, KeyModifiers.Control):
				workspace?.Scopes.StepCommitAsync(1).HandleExceptions();
				return true;
			case (Key.OemOpenBrackets, KeyModifiers.Control):
				workspace?.Scopes.StepCommitAsync(-1).HandleExceptions();
				return true;
			case (Key.OemCloseBrackets, KeyModifiers.None):
				workspace?.OpenAdjacentFileAsync(1).HandleExceptions();
				return true;
			case (Key.OemOpenBrackets, KeyModifiers.None):
				workspace?.OpenAdjacentFileAsync(-1).HandleExceptions();
				return true;
			case (Key.V, KeyModifiers.None):
				workspace?.ToggleViewedAndAdvanceAsync().HandleExceptions();
				return true;
			case (Key.O, KeyModifiers.None):
				workspace?.ToggleOverviewAsync().HandleExceptions();
				return true;
			case (Key.F12, KeyModifiers.None):
				view.GoToDefinitionCommand();
				return true;
			case (Key.F12, KeyModifiers.Shift):
				view.FindReferencesCommand();
				return true;
			case (Key.U, KeyModifiers.None):
				view.JumpToUncoveredCommand();
				return true;
			case (Key.B, KeyModifiers.None):
				view.ToggleBlameCommand();
				return true;
			case (Key.C, KeyModifiers.None):
				view.CommentAtCaretCommand();
				return true;
			case (Key.Left, KeyModifiers.Alt):
				workspace?.GoBackAsync().HandleExceptions();
				return true;
			case (Key.Right, KeyModifiers.Alt):
				workspace?.GoForwardAsync().HandleExceptions();
				return true;
			default:
				return false;
		}
	}

	/// <summary>
	/// The next hunk, or the next file when this one has no more. Reading a change is one pass
	/// through it, not a pass through each file with a stop at every border: the key that says
	/// "on to the next thing" should not stop meaning that at the end of a file, which is the
	/// one place a reader is certainly done with it.
	/// </summary>
	static void StepHunk(IReviewDocumentView view, int direction)
	{
		if (view.JumpToHunkCommand(direction))
			return;
		StepFileAsync(direction).HandleExceptions();
	}

	static async Task StepFileAsync(int direction)
	{
		if (App.Workspace is not { } workspace)
			return;
		await workspace.OpenAdjacentFileAsync(direction);
		// Whatever is in front now, which is the file just opened - and the edge it is entered
		// at follows the direction of travel. Queued behind the layout the open triggers: until
		// that has run there are no rows to land on.
		Avalonia.Threading.Dispatcher.UIThread.Post(
			() => ReviewViews.Active?.JumpToEdgeHunk(direction),
			Avalonia.Threading.DispatcherPriority.Loaded);
	}
}
