using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

using AvaloniaEdit.Highlighting;

namespace Stampeded.Editor;

/// <summary>
/// What a tooltip shows about the thing under the pointer: the signature in the colours the
/// file itself is read in, and whatever documentation came with it underneath.
///
/// The colours come from the grammar the editor already uses for the file, so a language is
/// coloured here as soon as it is coloured there - nothing knows about C# or Python in
/// particular. A semantic classification would be better for the few tokens a grammar reads
/// only as identifiers, but a signature is one line and this is what a reader compares it to.
/// </summary>
static class QuickInfoView
{
	/// <summary>Enough documentation to be worth reading, and not so much that the tooltip
	/// covers the code it is about.</summary>
	const int MaxDocumentationLines = 20;

	public static Control For(string text, string relPath, FontFamily codeFont)
	{
		var (signature, documentation) = Split(text);
		var panel = new StackPanel { MaxWidth = 720 };
		panel.Children.Add(Colored(signature, relPath, codeFont));
		if (documentation.Length > 0)
		{
			panel.Children.Add(new TextBlock {
				Text = documentation,
				TextWrapping = TextWrapping.Wrap,
				Opacity = 0.85,
				Margin = new Avalonia.Thickness(0, 6, 0, 0),
			});
		}
		return panel;
	}

	/// <summary>
	/// The signature and the rest. Servers and Roslyn agree on the shape without agreeing on
	/// anything else: what a symbol is comes first, its documentation after a blank line -
	/// and a signature runs over several lines as soon as it has parameters worth naming. A
	/// server that answers in markdown despite being asked for plain text fences the
	/// signature instead, and the fence is not part of it.
	/// </summary>
	static (string Signature, string Documentation) Split(string text)
	{
		var lines = text.Replace("\r\n", "\n").Split('\n');
		var signature = new List<string>();
		int at = 0;
		if (lines[0].StartsWith("```", StringComparison.Ordinal))
		{
			at = 1;
			while (at < lines.Length && !lines[at].StartsWith("```", StringComparison.Ordinal))
				signature.Add(lines[at++]);
			at++;
		}
		else
		{
			while (at < lines.Length && lines[at].Trim().Length > 0)
				signature.Add(lines[at++]);
		}
		var rest = lines.Skip(at)
			.SkipWhile(l => l.Trim().Length == 0 || l.StartsWith("```", StringComparison.Ordinal))
			.Take(MaxDocumentationLines)
			.ToList();
		return (string.Join("\n", signature).TrimEnd(), string.Join("\n", rest).TrimEnd());
	}

	static TextBlock Colored(string signature, string relPath, FontFamily codeFont)
	{
		var block = new TextBlock {
			FontFamily = codeFont,
			TextWrapping = TextWrapping.Wrap,
		};
		var lines = signature.Split('\n');
		var spans = SyntaxPainter.For(relPath)?.Paint(signature).ToLookup(s => s.Line);
		if (spans is null)
		{
			block.Text = signature;
			return block;
		}
		for (int number = 1; number <= lines.Length; number++)
		{
			string line = lines[number - 1];
			int at = 0;
			foreach (var span in spans[number].OrderBy(s => s.Start))
			{
				int length = Math.Min(span.Length, line.Length - span.Start);
				if (span.Start < at || length <= 0)
					continue;
				if (span.Start > at)
					block.Inlines!.Add(Run(line[at..span.Start], null));
				block.Inlines!.Add(Run(line.Substring(span.Start, length), span.Color));
				at = span.Start + length;
			}
			if (at < line.Length)
				block.Inlines!.Add(Run(line[at..], null));
			if (number < lines.Length)
				block.Inlines!.Add(new LineBreak());
		}
		return block;
	}

	static Run Run(string text, HighlightingColor? color)
	{
		var run = new Run(text);
		if (color?.Foreground?.GetBrush(null!) is { } brush)
			run.Foreground = brush;
		if (color?.FontWeight is { } weight)
			run.FontWeight = weight;
		if (color?.FontStyle is { } style)
			run.FontStyle = style;
		return run;
	}
}
