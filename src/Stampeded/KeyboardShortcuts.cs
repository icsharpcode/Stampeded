namespace Stampeded;

/// <summary>
/// The gesture sheet, shown from Help. A keyboard-driven tool that only documents its keys in
/// menu headers documents them to whoever already went looking; this is the one page that
/// answers "what can I press" without opening five menus.
///
/// The single letters are the reason it is worth writing down: they are handled by the window
/// rather than bound to menu items - a "v" typed into a comment has to stay a "v" - so they
/// never show up as a gesture next to a command the way Ctrl+W does.
/// </summary>
static class KeyboardShortcuts
{
	public const string Text = """
		Reading the change
		  n  /  Ctrl+Down      next hunk
		  p  /  Ctrl+Up        previous hunk
		  ]                    next file
		  [                    previous file
		  u                    next added line that no test covered
		  v                    mark the file viewed and move to the next one
		  o                    overview, and back to the file it was left from

		Reading one commit at a time
		  Ctrl+]               next commit
		  Ctrl+[               previous commit

		Understanding the code
		  F12                  go to definition
		  Shift+F12            find references
		  Alt+Left             back
		  Alt+Right            forward
		  b                    blame margin on or off
		  Esc                  clear highlighted occurrences

		Saying something
		  c                    comment on the line at the caret
		  Ctrl+Enter           save the comment being written
		  Esc                  discard the comment being written

		The review itself
		  F5                   read the review again at the head it has now

		The window
		  Ctrl+W               close the tab in front
		  Ctrl++  /  Ctrl+-    zoom in and out
		  Ctrl+0               reset the zoom

		A single letter is a letter wherever text is being typed: these act only when the
		focus is not in a text box.
		""";
}
