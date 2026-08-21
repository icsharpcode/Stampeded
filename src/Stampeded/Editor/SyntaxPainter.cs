using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;

using Stampeded.Core.Infra;
using Stampeded.Themes;

using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Stampeded.Editor;

/// <summary>One coloured run of one line of a text: which line (1-based), where in the line it
/// starts (0-based), how long it is, and how it is drawn.</summary>
readonly record struct ColoredSpan(int Line, int Start, int Length, HighlightingColor Color);

/// <summary>
/// The colours of one text, line by line.
///
/// A text rather than a document, because the document on screen is a unified diff: the two
/// sides interleaved, which parses as neither of them. Every grammar is a state machine over
/// consecutive lines - a string that opens on one line and closes three lines down - so each
/// side is painted on its own text and the runs are carried onto the rows that text ended up
/// on (<see cref="DiffSyntaxColors"/>).
///
/// TextMate grammars first, because VS Code standardised on them and there is one for every
/// language a review might contain; the editor's own xshd definitions answer for what the
/// bundle does not have, which is ILAsm.
/// </summary>
abstract class SyntaxPainter
{
	public abstract IEnumerable<ColoredSpan> Paint(string text);

	/// <summary>
	/// How to paint a file. By extension where the extension says something, and otherwise by
	/// what the content turns out to be: a repository is full of XML nothing claims by name -
	/// .props, .targets, .axaml, .slnx, .resx - and of JSON under whatever extension the tool
	/// that wrote it chose, and grey text is a poor way to read either.
	/// </summary>
	/// <param name="content">Read only when the extension answered nothing: it is the whole
	/// file, and building it to look at a .cs would be paying for an answer already known.</param>
	public static SyntaxPainter? For(string path, Func<string> content)
	{
		if (TextMateGrammars.ByExtension(Path.GetExtension(path)) is { } byExtension)
			return byExtension;
		string? text = null;
		string Content() => text ??= content();
		string? language = GuessFileType.DetectTextType(Content()) switch {
			FileType.Xml => "xml",
			FileType.Json => "json",
			_ => null,
		};
		if (language is not null && TextMateGrammars.ByLanguage(language) is { } byContent)
			return byContent;
		return HighlightingService.GetForFile(path, Content) is { } definition
			? new XshdPainter(definition)
			: null;
	}

	/// <summary>How to paint a fragment of a language, for a signature in a tooltip: what the
	/// file is called is all there is to go on, and there is no content to sniff.</summary>
	public static SyntaxPainter? For(string path) => For(path, () => "");
}

/// <summary>What the editor's own highlighting definitions colour, for the formats the
/// TextMate bundle does not carry.</summary>
sealed class XshdPainter(IHighlightingDefinition definition) : SyntaxPainter
{
	public override IEnumerable<ColoredSpan> Paint(string text)
	{
		var document = new TextDocument(text);
		using var highlighter = new DocumentHighlighter(document, definition);
		highlighter.BeginHighlighting();
		for (int number = 1; number <= document.LineCount; number++)
		{
			var line = document.GetLineByNumber(number);
			foreach (var section in highlighter.HighlightLine(number).Sections)
				yield return new ColoredSpan(number, section.Offset - line.Offset, section.Length, section.Color);
		}
		highlighter.EndHighlighting();
	}
}

/// <summary>A TextMate grammar and the theme its scopes are looked up in.</summary>
sealed class TextMatePainter(IGrammar grammar, Theme theme) : SyntaxPainter
{
	/// <summary>A grammar is regular expressions, and a pathological line can take arbitrarily
	/// long in one. A line that cannot be tokenised in this long is left uncoloured rather than
	/// stalling the view it is drawn in.</summary>
	static readonly TimeSpan PerLine = TimeSpan.FromMilliseconds(100);

	readonly Dictionary<(int Foreground, FontStyle Style), HighlightingColor> colors = [];

	public override IEnumerable<ColoredSpan> Paint(string text)
	{
		IStateStack? carried = null;
		int number = 0;
		foreach (string line in text.Split('\n'))
		{
			number++;
			// The grammar is given the line as the file has it; a carriage return left on the
			// end is a character it would have to account for and no grammar mentions one.
			string body = line.TrimEnd('\r');
			var tokenized = carried is null
				? grammar.TokenizeLine(body)
				: grammar.TokenizeLine(body, carried, PerLine);
			carried = tokenized.RuleStack;
			foreach (var token in tokenized.Tokens)
			{
				int length = Math.Min(token.EndIndex, body.Length) - token.StartIndex;
				if (length > 0 && ColorOf(token.Scopes) is { } color)
					yield return new ColoredSpan(number, token.StartIndex, length, color);
			}
		}
	}

	/// <summary>
	/// The colour a token's scopes resolve to in this theme. A token carries its scopes from
	/// the outermost in, and the theme's own matching returns what applies most specifically
	/// first, so the first rule that names a foreground is the answer.
	/// </summary>
	HighlightingColor? ColorOf(IList<string> scopes)
	{
		foreach (var rule in theme.Match(scopes))
		{
			if (rule.foreground <= 0)
				continue;
			var key = (rule.foreground, rule.fontStyle);
			if (!colors.TryGetValue(key, out var color))
			{
				color = Build(theme.GetColor(rule.foreground), rule.fontStyle);
				colors[key] = color;
			}
			return color;
		}
		return null;
	}

	static HighlightingColor Build(string? foreground, FontStyle style)
	{
		var color = new HighlightingColor();
		if (foreground is { Length: > 0 })
			color.Foreground = new SimpleHighlightingBrush(Avalonia.Media.Color.Parse(foreground));
		if (style > 0 && (style & FontStyle.Bold) != 0)
			color.FontWeight = Avalonia.Media.FontWeight.Bold;
		if (style > 0 && (style & FontStyle.Italic) != 0)
			color.FontStyle = Avalonia.Media.FontStyle.Italic;
		color.Freeze();
		return color;
	}
}

/// <summary>
/// The grammar bundle and the theme, built once per theme. Both are shared by every view: a
/// registry compiles a grammar the first time it is asked for one, and a review opens many
/// files of the same language.
/// </summary>
static class TextMateGrammars
{
	static readonly Lock gate = new();
	static RegistryOptions? options;
	static Registry? registry;
	static Theme? theme;
	static bool dark;
	static readonly Dictionary<string, TextMatePainter?> byScope = new(StringComparer.OrdinalIgnoreCase);

	public static SyntaxPainter? ByExtension(string extension)
		=> extension.Length == 0 ? null : ForScope(o => o.GetScopeByExtension(extension));

	public static SyntaxPainter? ByLanguage(string languageId)
		=> ForScope(o => o.GetScopeByLanguageId(languageId));

	static SyntaxPainter? ForScope(Func<RegistryOptions, string?> scopeOf)
	{
		lock (gate)
		{
			// The theme decides every colour in the bundle, so a change of it is a change of
			// everything painted from here.
			if (registry is null || dark != ThemeManager.Current.IsDarkTheme)
			{
				dark = ThemeManager.Current.IsDarkTheme;
				options = new RegistryOptions(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
				registry = new Registry(options);
				theme = registry.GetTheme();
				byScope.Clear();
			}
			if (scopeOf(options!) is not { Length: > 0 } scope)
				return null;
			if (!byScope.TryGetValue(scope, out var painter))
			{
				var grammar = registry.LoadGrammar(scope);
				painter = grammar is null ? null : new TextMatePainter(grammar, theme!);
				byScope[scope] = painter;
			}
			return painter;
		}
	}
}
