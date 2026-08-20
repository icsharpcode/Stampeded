namespace Stampeded.Core.Lsp;

/// <summary>
/// A file's text with its line starts, for the conversions a language server does not do:
/// offsets to (line, column) and back, the text of one line, and the word under a position -
/// which is as close to "the symbol here" as a client gets before it asks the server.
///
/// Lines and columns are 1-based, the way the diff and the editor talk about them; LSP's own
/// 0-based pairs are converted at the request boundary.
/// </summary>
sealed class TextIndex
{
	readonly int[] lineStarts;

	public TextIndex(string text)
	{
		Text = text;
		var starts = new List<int> { 0 };
		for (int i = 0; i < text.Length; i++)
		{
			if (text[i] == '\n')
				starts.Add(i + 1);
		}
		lineStarts = [.. starts];
	}

	public string Text { get; }

	public int LineCount => lineStarts.Length;

	public int? OffsetOf(int line, int column)
	{
		if (line < 1 || line > lineStarts.Length)
			return null;
		int start = lineStarts[line - 1];
		return start + Math.Clamp(column - 1, 0, LineLength(line));
	}

	public (int Line, int Column)? LineColumnOf(int offset)
	{
		if (offset < 0 || offset > Text.Length)
			return null;
		int low = 0, high = lineStarts.Length - 1;
		while (low < high)
		{
			int middle = (low + high + 1) / 2;
			if (lineStarts[middle] <= offset)
				low = middle;
			else
				high = middle - 1;
		}
		return (low + 1, offset - lineStarts[low] + 1);
	}

	public string LineText(int line)
	{
		if (line < 1 || line > lineStarts.Length)
			return "";
		int start = lineStarts[line - 1];
		return Text.Substring(start, LineLength(line));
	}

	int LineLength(int line)
	{
		int start = lineStarts[line - 1];
		int end = line < lineStarts.Length ? lineStarts[line] - 1 : Text.Length;
		if (end > start && Text[end - 1] == '\r')
			end--;
		return Math.Max(0, end - start);
	}

	/// <summary>The identifier the column is inside, with the column it starts at; null when
	/// the position is on whitespace or punctuation.</summary>
	public (string Text, int Column)? WordAt(int line, int column)
	{
		string text = LineText(line);
		int index = column - 1;
		if (index < 0 || index >= text.Length || !IsWordChar(text[index]))
			return null;
		int start = index;
		while (start > 0 && IsWordChar(text[start - 1]))
			start--;
		int end = index;
		while (end + 1 < text.Length && IsWordChar(text[end + 1]))
			end++;
		return (text[start..(end + 1)], start + 1);
	}

	/// <summary>The first identifier on a line, for a caret left in the indentation.</summary>
	public (string Text, int Column)? FirstWordOn(int line)
	{
		string text = LineText(line);
		for (int i = 0; i < text.Length; i++)
		{
			if (IsWordChar(text[i]) && !char.IsDigit(text[i]))
				return WordAt(line, i + 1);
		}
		return null;
	}

	static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
