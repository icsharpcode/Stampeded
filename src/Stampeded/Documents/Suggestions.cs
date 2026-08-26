using Avalonia.Controls;

namespace Stampeded.Documents;

/// <summary>
/// A suggested change: the fenced "suggestion" block GitHub reads out of a review comment. It
/// renders as a diff of the commented line against what the block holds, and the author can
/// commit it with a button - so a remark about one line can be the line itself rather than a
/// description of what it should say.
///
/// Nothing about submitting changes: it is an ordinary comment body, and the review posts it
/// the way it posts any other.
/// </summary>
static class Suggestion
{
	const string Fence = "```suggestion";

	/// <summary>
	/// Opens the comment editor as a suggestion: the block, prefilled with the line it would
	/// replace and the caret at the end of it. Prefilled rather than empty because a suggestion
	/// is nearly always an edit of that line, and an empty block means typing it out again to
	/// change one word of it.
	/// </summary>
	public static void Prefill(TextBox box, CommentTarget? target)
	{
		if (target is null)
			return;
		if (target.OldSide)
		{
			// The block replaces a line of the head, and a line of the base has none to replace:
			// GitHub would render the suggestion and refuse to apply it.
			App.Workspace?.PostStatus("A suggestion replaces a line of the new file, and this comment "
				+ "is on the base side. Comment on the line as it stands in the head instead.");
			return;
		}
		string body = box.Text ?? "";
		if (body.Contains(Fence, StringComparison.Ordinal))
		{
			// GitHub applies one suggestion per comment, so a second block is one nobody can
			// accept. Two suggestions are two comments.
			App.Workspace?.PostStatus("This comment already suggests a change; GitHub applies one per comment.");
			return;
		}
		box.Text = $"{Fence}\n{target.LineText}\n```\n{body}";
		box.CaretIndex = Fence.Length + 1 + target.LineText.Length;
		box.Focus();
	}
}
