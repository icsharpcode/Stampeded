using Stampeded.Core.Diff;

namespace Stampeded.Documents;

/// <summary>
/// A file's change as a document, in whichever layout it is being read. The workspace opens
/// and navigates through this, so that everything which used to mean "the unified document" -
/// go to a line, step to the next file, walk back through history - means "the document of
/// this file" instead.
/// </summary>
public interface IDiffDocument
{
	FileDiff File { get; }

	/// <summary>The dockable id, which history records so it can find its way back here. Both
	/// layouts of a file carry the same one: it is the same document, laid out differently.
	/// </summary>
	string? Id { get; }

	/// <summary>Asks for the caret at a line of the file, on the given side. In file
	/// coordinates, not the document's: a document holds rows that belong to neither blob -
	/// spliced comment threads, filler - and a request made in its own numbering would land
	/// wherever those pushed it.</summary>
	void RequestCaret(int blobLine, bool oldSide = false);
}
