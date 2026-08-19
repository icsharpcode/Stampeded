using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.LogicalTree;

using ColorTextBlock.Avalonia;

namespace Stampeded.Controls;

/// <summary>
/// Emphasis that reaches around a link or a code span, which the markdown renderer draws as
/// its own markers.
///
/// The renderer resolves links, images and code spans first and folds "*" and "_" into
/// whatever plain text is left over afterwards. A span that holds one of the others is
/// therefore never seen whole: "**see `Foo`**" arrives as the text "**see ", a code span, and
/// the text "** ", and each fragment is searched for a pair of markers on its own and finds
/// none. Reviewers write exactly that - a bold reference to a symbol, an emphasized link to an
/// issue - and it renders with the asterisks showing.
///
/// So the markers are paired again afterwards, across the fragments the renderer produced,
/// and what lies between them is wrapped. Only a pair with something the renderer built
/// between it is joined: a pair inside one piece of text is a pair the renderer already
/// declined to read as emphasis, and re-reading it here would turn "2 * 3 * 4" into prose.
/// </summary>
public static class MarkdownEmphasis
{
	/// <summary>Repairs every block of rendered markdown below <paramref name="root"/>.</summary>
	public static void Repair(Control root)
	{
		foreach (var block in root.GetLogicalDescendants().OfType<CTextBlock>())
		{
			var original = block.Content.ToList();
			if (!MayHold(original))
				continue;
			// Emptied before it is rebuilt: what comes back holds the same inlines, and an
			// inline still owned by the block it is being added to again is a child of two
			// parents - which is the state Avalonia refuses to attach.
			block.Content = [];
			var repaired = Repair(original);
			block.Content = new AvaloniaList<CInline>(repaired ?? original);
		}
	}

	/// <summary>Whether a block could hold emphasis the renderer left as markers: a marker
	/// character in its text and something built beside it to have reached around. Asked
	/// before anything is taken apart, because most blocks hold neither.</summary>
	static bool MayHold(IEnumerable<CInline> inlines)
	{
		bool marker = false, built = false;
		foreach (var inline in inlines)
		{
			if (inline is CRun run)
				marker |= run.Text.AsSpan().IndexOfAny('*', '_') >= 0;
			else
				built = true;
			if (inline is CSpan span && MayHold(span.Content))
				return true;
		}
		return marker && built;
	}

	/// <summary>The inlines with their emphasis restored, or null when nothing changed -
	/// which is the common case, and worth not rebuilding a block for.</summary>
	static List<CInline>? Repair(IEnumerable<CInline> inlines)
	{
		bool changed = false;
		var tokens = new List<Token>();
		foreach (var inline in inlines)
		{
			// A hyperlink or a colored span holds inlines of its own, parsed by the same
			// leftover pass and left in the same state. Its content is replaced while the
			// span is detached from the block, so nothing is parented twice.
			if (inline is CSpan span and not CCode && Repair(span.Content.ToList()) is { } inner)
			{
				span.Content = inner;
				changed = true;
			}
			if (inline is CRun run)
				Tokenize(run.Text, tokens);
			else
				tokens.Add(Token.Of(inline));
		}
		changed |= Pair(tokens);
		return changed ? Materialize(tokens) : null;
	}

	/// <summary>Splits a run into its text and the emphasis markers in it. A marker is written
	/// down even where it cannot be one: whether it opens or closes is decided later, from
	/// what ends up on either side of it.</summary>
	static void Tokenize(string text, List<Token> tokens)
	{
		int start = 0;
		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] is not ('*' or '_'))
				continue;
			int length = i + 1 < text.Length && text[i + 1] == text[i] ? 2 : 1;
			if (i > start)
				tokens.Add(Token.Of(text[start..i]));
			tokens.Add(Token.Marker(text.Substring(i, length)));
			i += length - 1;
			start = i + 1;
		}
		if (start < text.Length)
			tokens.Add(Token.Of(text[start..]));
	}

	/// <summary>Joins what pairs. Leftmost first, and what a pair encloses is paired again
	/// inside it, so "**a `b` *c* d**" comes out bold with an italic in it.</summary>
	static bool Pair(List<Token> tokens)
	{
		bool changed = false;
		for (int open = 0; open < tokens.Count; open++)
		{
			if (tokens[open].Mark is not { } mark || !CanOpen(tokens, open))
				continue;
			int close = FindClose(tokens, open, mark);
			if (close < 0 || !HoldsInline(tokens, open, close))
				continue;
			var inner = tokens.GetRange(open + 1, close - open - 1);
			Pair(inner);
			var content = Materialize(inner);
			tokens.RemoveRange(open, close - open + 1);
			tokens.Insert(open, Token.Of(mark.Length == 2
				? new CBold(content)
				: (CInline)new CItalic(content)));
			changed = true;
		}
		return changed;
	}

	static int FindClose(List<Token> tokens, int open, string mark)
	{
		for (int i = open + 1; i < tokens.Count; i++)
		{
			if (tokens[i].Mark == mark && CanClose(tokens, i))
				return i;
		}
		return -1;
	}

	/// <summary>Whether the renderer built something between the two markers. That is what
	/// tells a pair it could not read from one it read and rejected.</summary>
	static bool HoldsInline(List<Token> tokens, int open, int close)
	{
		for (int i = open + 1; i < close; i++)
		{
			if (tokens[i].Inline is not null)
				return true;
		}
		return false;
	}

	/// <summary>Emphasis opens where it is followed by something, not by a space - "a * b" is
	/// multiplication. An underscore also has to start a word: it is a letter as far as
	/// snake_case is concerned, and two identifiers in one sentence are not emphasis.</summary>
	static bool CanOpen(List<Token> tokens, int index)
	{
		if (Following(tokens, index) is { } next && char.IsWhiteSpace(next))
			return false;
		return tokens[index].Mark![0] != '_'
			|| Preceding(tokens, index) is not { } previous
			|| !char.IsLetterOrDigit(previous);
	}

	static bool CanClose(List<Token> tokens, int index)
	{
		if (Preceding(tokens, index) is { } previous && char.IsWhiteSpace(previous))
			return false;
		return tokens[index].Mark![0] != '_'
			|| Following(tokens, index) is not { } next
			|| !char.IsLetterOrDigit(next);
	}

	/// <summary>The character on either side of a marker, or null when what is there is
	/// something the renderer built rather than text - which is never a space and never part
	/// of a word.</summary>
	static char? Preceding(List<Token> tokens, int index)
		=> index > 0 && tokens[index - 1].Text is { Length: > 0 } text ? text[^1] : null;

	static char? Following(List<Token> tokens, int index)
		=> index + 1 < tokens.Count && tokens[index + 1].Text is { Length: > 0 } text ? text[0] : null;

	/// <summary>The tokens as inlines again. A marker that found no partner is what it was
	/// written as: text.</summary>
	static List<CInline> Materialize(List<Token> tokens)
	{
		var inlines = new List<CInline>();
		var pending = new System.Text.StringBuilder();
		foreach (var token in tokens)
		{
			if (token.Inline is { } inline)
			{
				Flush();
				inlines.Add(inline);
			}
			else
			{
				pending.Append(token.Text ?? token.Mark);
			}
		}
		Flush();
		return inlines;

		void Flush()
		{
			if (pending.Length == 0)
				return;
			inlines.Add(new CRun { Text = pending.ToString() });
			pending.Clear();
		}
	}

	/// <summary>One piece of a block: text, a marker in it, or something already built.</summary>
	readonly record struct Token(string? Text, string? Mark, CInline? Inline)
	{
		public static Token Of(string text) => new(text, null, null);

		public static Token Of(CInline inline) => new(null, null, inline);

		public static Token Marker(string mark) => new(null, mark, null);
	}
}
