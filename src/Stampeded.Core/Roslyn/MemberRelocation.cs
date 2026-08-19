namespace Stampeded.Core.Roslyn;

/// <summary>Where a comment ended up: the line it now names, the member it was written in as
/// that member is called, and whether the commented line itself was found there or only the
/// member was.</summary>
public sealed record MemberMove(int Line, string Member, bool FoundTheLine);

/// <summary>
/// Moves a comment whose line is gone to where the code it was about now lives.
///
/// A comment is written about something - a method, a property, a type - and code moves: a
/// member gains lines above it, or is pushed down the file by an edit somewhere else. Matching
/// by line text and a window of context, which is what a comment carries, then finds nothing
/// and the remark is reported as outdated although the thing it is about is right there.
///
/// So the member is asked instead. The blob the comment was written against says which member
/// the line was in; the blob on screen says where that member is now, and the line is looked
/// for inside it. Nothing here needs the two blobs to be related - it works across a rebase or
/// a force-push as long as the old one can still be read.
/// </summary>
public static class MemberRelocation
{
	/// <summary>
	/// The line <paramref name="oldLine"/> of <paramref name="oldText"/> corresponds to in
	/// <paramref name="newText"/>, or null when the member it was in is not there any more.
	/// Both texts are C#; the outline is a syntax-only parse and tolerates broken code.
	/// </summary>
	public static MemberMove? Locate(string oldText, int oldLine, string newText, string lineText)
	{
		var oldPath = PathTo(DocumentOutline.Compute(oldText), oldLine);
		if (oldPath.Count == 0)
			return null;
		if (Find(DocumentOutline.Compute(newText), oldPath) is not { } member)
			return null;
		var newLines = newText.ReplaceLineEndings("\n").Split('\n');
		string name = string.Join(" > ", oldPath.Select(n => n.Title));
		// The line itself, if it is still in there: an edit above it inside the same member
		// moves it without changing it, which is the common case and the exact answer.
		if (lineText.Trim().Length > 0)
		{
			int found = -1;
			for (int line = member.StartLine; line <= Math.Min(member.EndLine, newLines.Length); line++)
			{
				if (!newLines[line - 1].Trim().Equals(lineText.Trim(), StringComparison.Ordinal))
					continue;
				// The first match unless a later one sits where the line used to, which is what
				// tells two identical lines of the same member apart.
				if (found < 0 || Math.Abs(line - oldLine) < Math.Abs(found - oldLine))
					found = line;
			}
			if (found > 0)
				return new MemberMove(found, name, FoundTheLine: true);
		}
		// The line is gone but the member is not: the same distance into it, which keeps a
		// remark about the third statement of a method next to the third statement. A member
		// that has since become shorter than that takes the remark on its first line - the
		// declaration is what the comment is about once the statement it named is gone, and a
		// closing brace says nothing.
		int offset = Math.Max(0, oldLine - oldPath[^1].StartLine);
		int placed = member.StartLine + offset;
		if (placed >= member.EndLine)
			placed = member.StartLine;
		return new MemberMove(placed, name, FoundTheLine: false);
	}

	/// <summary>The chain of outline nodes containing a line, outermost first. Empty when the
	/// line is outside every member - a using directive, a file-level comment - where there is
	/// nothing to follow.</summary>
	static List<OutlineNode> PathTo(IReadOnlyList<OutlineNode> nodes, int line)
	{
		foreach (var node in nodes)
		{
			if (line < node.StartLine || line > node.EndLine)
				continue;
			var path = new List<OutlineNode> { node };
			path.AddRange(PathTo(node.Children, line));
			return path;
		}
		return [];
	}

	/// <summary>
	/// The same member in another version of the file. The whole chain first - a method of the
	/// right type, not one of the same name in a neighbouring type - and, failing that, the
	/// innermost member wherever it now sits, since a type being renamed or a member moving
	/// between the types of one file leaves the member itself intact.
	/// </summary>
	static OutlineNode? Find(IReadOnlyList<OutlineNode> nodes, List<OutlineNode> path)
	{
		var level = nodes;
		OutlineNode? match = null;
		foreach (var step in path)
		{
			if (Best(level, step) is not { } next)
			{
				match = null;
				break;
			}
			match = next;
			level = next.Children;
		}
		return match ?? Anywhere(nodes, path[^1]);
	}

	/// <summary>
	/// The candidate at one level that is the same member. An identical signature wins; after
	/// that a member of the same kind and name, because a parameter added to a method is the
	/// same method - which is exactly the change a review is being written about. Among
	/// overloads the most similar parameter list wins, so a remark about one of them does not
	/// wander into another.
	/// </summary>
	static OutlineNode? Best(IReadOnlyList<OutlineNode> level, OutlineNode wanted)
		=> level.FirstOrDefault(n => n.Kind == wanted.Kind && n.Title == wanted.Title)
			?? level.Where(n => n.Kind == wanted.Kind && Name(n.Title) == Name(wanted.Title))
				.OrderByDescending(n => Shared(n.Title, wanted.Title))
				.FirstOrDefault();

	/// <summary>A member's name without its parameter list: what survives a signature
	/// change.</summary>
	static string Name(string title)
	{
		int open = title.IndexOf('(');
		return open < 0 ? title : title[..open];
	}

	/// <summary>How much of two signatures reads the same from the left - enough to tell
	/// overloads apart without pretending to compare types.</summary>
	static int Shared(string a, string b)
	{
		int i = 0;
		while (i < a.Length && i < b.Length && a[i] == b[i])
			i++;
		return i;
	}

	static OutlineNode? Anywhere(IReadOnlyList<OutlineNode> nodes, OutlineNode wanted)
	{
		if (Best(nodes, wanted) is { } here)
			return here;
		foreach (var node in nodes)
		{
			if (Anywhere(node.Children, wanted) is { } nested)
				return nested;
		}
		return null;
	}
}
