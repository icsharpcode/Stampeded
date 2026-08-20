using System.Text.Json;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Lsp;

/// <summary>
/// One side of a review answered by a language server.
///
/// The interface asks about positions in repository-relative files; LSP answers about ranges
/// in URIs, and knows nothing about symbols as objects. The gap is bridged here, and in one
/// direction only: this class holds the text of every file it has talked about, because a
/// position given as an offset has to become a (line, character) pair, a reference has to
/// come back with the line it sits on, and "the symbol at the caret" is the word under it -
/// there is no server request that answers what a token is called.
/// </summary>
public sealed class LspSemanticProvider : ISemanticProvider
{
	readonly LspConnection connection;
	readonly string rootPath;
	readonly string name;
	readonly Dictionary<string, TextIndex> openDocuments = new(StringComparer.Ordinal);
	readonly Dictionary<string, string> overlay = new(StringComparer.Ordinal);
	readonly string[] tokenTypes;
	SemanticState state = SemanticState.Loading;

	public LspSemanticProvider(LspConnection connection, string rootPath, string name)
	{
		this.connection = connection;
		this.rootPath = rootPath;
		this.name = name;
		tokenTypes = ReadTokenLegend(connection.Capabilities);
		state = SemanticState.Ready;
	}

	public SemanticState State => state;

	public string StateDetail => name;

	public string LoadLog => "";

	public event Action? StateChanged;

	public string ToAbsolutePath(string repoRelativePath)
		=> Path.GetFullPath(Path.Combine(rootPath, repoRelativePath.Replace('/', Path.DirectorySeparatorChar)));

	public string? ToRelativePath(string absolutePath)
	{
		string full = Path.GetFullPath(absolutePath);
		string root = Path.GetFullPath(rootPath);
		if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
			return null;
		return full[root.Length..].TrimStart(Path.DirectorySeparatorChar, '/').Replace(Path.DirectorySeparatorChar, '/');
	}

	#region Documents

	/// <summary>
	/// The server's copy of a file, opened on first use and kept open. Servers answer about
	/// what they were told, not about what is on disk - pyright will not resolve a position
	/// in a file it was never handed - so every question about a file goes through here.
	/// </summary>
	TextIndex? Open(string relPath)
	{
		if (openDocuments.TryGetValue(relPath, out var known))
			return known;
		string? text = overlay.TryGetValue(relPath, out var overlaid) ? overlaid : ReadFile(relPath);
		if (text is null)
			return null;
		var index = new TextIndex(text);
		openDocuments[relPath] = index;
		connection.Notify("textDocument/didOpen", new {
			textDocument = new {
				uri = LspUri.FromPath(ToAbsolutePath(relPath)),
				languageId = LanguageIdOf(relPath),
				version = 1,
				text,
			},
		});
		return index;
	}

	string? ReadFile(string relPath)
	{
		try
		{
			string path = ToAbsolutePath(relPath);
			return File.Exists(path) ? File.ReadAllText(path) : null;
		}
		catch (IOException ex)
		{
			CliLog.Write(name, $"read {relPath} FAILED: {ex.Message}");
			return null;
		}
	}

	static string LanguageIdOf(string relPath) => Path.GetExtension(relPath).ToLowerInvariant() switch {
		".py" or ".pyi" => "python",
		".cs" => "csharp",
		".ts" => "typescript",
		".js" => "javascript",
		".go" => "go",
		".rs" => "rust",
		_ => "plaintext",
	};

	public void SetTextOverlay(IReadOnlyDictionary<string, string> textByRelativePath)
	{
		overlay.Clear();
		foreach (var (relPath, text) in textByRelativePath)
			overlay[relPath] = text;
		foreach (var relPath in openDocuments.Keys.ToList())
			Resend(relPath, overlay.TryGetValue(relPath, out var text) ? text : ReadFile(relPath));
	}

	public void ClearTextOverlay()
	{
		var wasOverlaid = overlay.Keys.ToList();
		overlay.Clear();
		foreach (var relPath in wasOverlaid)
			Resend(relPath, ReadFile(relPath));
	}

	/// <summary>Replaces the server's copy of a file wholesale. Incremental sync would save
	/// bytes on a keystroke; nothing here types, it swaps one revision for another.</summary>
	void Resend(string relPath, string? text)
	{
		if (text is null || !openDocuments.ContainsKey(relPath))
			return;
		openDocuments[relPath] = new TextIndex(text);
		connection.Notify("textDocument/didChange", new {
			textDocument = new { uri = LspUri.FromPath(ToAbsolutePath(relPath)), version = 2 },
			contentChanges = new[] { new { text } },
		});
	}

	public Task<string?> GetDocumentTextAsync(string relPath, CancellationToken ct)
		=> Task.FromResult(Open(relPath)?.Text);

	public Task<int?> GetPositionAsync(string relPath, int line, int column, CancellationToken ct)
		=> Task.FromResult(Open(relPath)?.OffsetOf(line, column));

	#endregion

	#region Symbols as words under a position

	public Task<SymbolRef?> GetSymbolAtAsync(string relPath, int position, CancellationToken ct)
	{
		if (Open(relPath) is not { } index || index.LineColumnOf(position) is not { } at)
			return Task.FromResult<SymbolRef?>(null);
		var word = index.WordAt(at.Line, at.Column);
		return Task.FromResult(word is null ? null : Make(relPath, at.Line, word.Value.Column, word.Value.Text));
	}

	public Task<SymbolRef?> GetSymbolOnLineAsync(
		string relPath, int line, int preferredColumn, CancellationToken ct)
	{
		if (Open(relPath) is not { } index)
			return Task.FromResult<SymbolRef?>(null);
		var word = index.WordAt(line, preferredColumn) ?? index.FirstWordOn(line);
		return Task.FromResult(word is null ? null : Make(relPath, line, word.Value.Column, word.Value.Text));
	}

	static SymbolRef Make(string relPath, int line, int column, string word)
		=> new(relPath, line, column, word, word, IsType: false, ContainingType: null);

	public async Task<SymbolRef?> GetEnclosingMemberAsync(string relPath, int line, CancellationToken ct)
	{
		var symbols = await DocumentSymbolsAsync(relPath, ct);
		var member = Innermost(symbols, line);
		if (member is null)
			return null;
		var containing = symbols
			.Where(s => s != member && s.StartLine <= line && line <= s.EndLine && LspSymbolKinds.IsType(s.Kind))
			.OrderByDescending(s => s.StartLine)
			.FirstOrDefault();
		return new SymbolRef(relPath, member.SelectionLine, member.SelectionColumn, member.Display, member.Name,
			LspSymbolKinds.IsType(member.Kind),
			containing is null
				? null
				: new SymbolRef(relPath, containing.SelectionLine, containing.SelectionColumn,
					containing.Display, containing.Name, true, null));
	}

	#endregion

	#region Positional requests

	public async Task<SymbolLocation?> GetDefinitionAsync(SymbolRef symbol, CancellationToken ct)
	{
		Open(symbol.RelPath);
		var result = await connection.RequestAsync("textDocument/definition", new {
			textDocument = new { uri = LspUri.FromPath(ToAbsolutePath(symbol.RelPath)) },
			position = new { line = symbol.Line - 1, character = symbol.Column - 1 },
		}, ct);
		foreach (var location in Locations(result))
			return location;
		return null;
	}

	public async Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(SymbolRef symbol, CancellationToken ct)
	{
		Open(symbol.RelPath);
		var result = await connection.RequestAsync("textDocument/references", new {
			textDocument = new { uri = LspUri.FromPath(ToAbsolutePath(symbol.RelPath)) },
			position = new { line = symbol.Line - 1, character = symbol.Column - 1 },
			context = new { includeDeclaration = true },
		}, ct);
		var hits = new List<ReferenceHit>();
		foreach (var location in Locations(result))
		{
			hits.Add(new ReferenceHit(
				location.FilePath, location.Line, location.Column, location.Length,
				LineTextOf(location.FilePath, location.Line)));
		}
		return hits
			.DistinctBy(h => (h.FilePath, h.Line, h.Column))
			.OrderBy(h => h.FilePath, StringComparer.Ordinal)
			.ThenBy(h => h.Line)
			.ToList();
	}

	public async Task<IReadOnlyList<SemanticToken>> FindOccurrencesInFileAsync(
		SymbolRef symbol, string relPath, CancellationToken ct)
	{
		Open(relPath);
		var result = await connection.RequestAsync("textDocument/documentHighlight", new {
			textDocument = new { uri = LspUri.FromPath(ToAbsolutePath(relPath)) },
			position = new { line = symbol.Line - 1, character = symbol.Column - 1 },
		}, ct);
		if (result.ValueKind != JsonValueKind.Array)
			return [];
		var occurrences = new List<SemanticToken>();
		foreach (var highlight in result.EnumerateArray())
		{
			if (!highlight.TryGetProperty("range", out var range))
				continue;
			var (line, column, length) = RangeOf(range);
			if (length <= 0)
				continue;
			// LSP's kind is Text/Read/Write; the view colours a definition apart from a use,
			// and Write is the closest thing a server says about which one it is.
			string kind = highlight.TryGetProperty("kind", out var k) && k.TryGetInt32(out int value) && value == 3
				? "definition"
				: "reference";
			occurrences.Add(new SemanticToken(line, column, length, kind));
		}
		return occurrences.DistinctBy(t => (t.Line, t.Column)).OrderBy(t => t.Line).ThenBy(t => t.Column).ToList();
	}

	public async Task<string?> GetQuickInfoAsync(string relPath, int position, CancellationToken ct)
	{
		string? hover = await GetHoverTextAsync(relPath, position, ct);
		return hover?.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim();
	}

	public async Task<string?> GetHoverTextAsync(string relPath, int position, CancellationToken ct)
	{
		if (Open(relPath) is not { } index || index.LineColumnOf(position) is not { } at)
			return null;
		var result = await connection.RequestAsync("textDocument/hover", new {
			textDocument = new { uri = LspUri.FromPath(ToAbsolutePath(relPath)) },
			position = new { line = at.Line - 1, character = at.Column - 1 },
		}, ct);
		if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents))
			return null;
		string text = ContentsText(contents).Trim();
		return text.Length == 0 ? null : text;
	}

	/// <summary>Hover contents come in three shapes across protocol versions: a marked
	/// string, an array of them, or a markup object.</summary>
	static string ContentsText(JsonElement contents) => contents.ValueKind switch {
		JsonValueKind.String => contents.GetString() ?? "",
		JsonValueKind.Array => string.Join("\n", contents.EnumerateArray().Select(ContentsText)),
		JsonValueKind.Object when contents.TryGetProperty("value", out var value) => value.GetString() ?? "",
		_ => "",
	};

	#endregion

	#region Whole-file and whole-workspace requests

	public async Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(string relPath, CancellationToken ct)
	{
		if (tokenTypes.Length == 0 || Open(relPath) is null)
			return [];
		var result = await connection.RequestAsync("textDocument/semanticTokens/full", new {
			textDocument = new { uri = LspUri.FromPath(ToAbsolutePath(relPath)) },
		}, ct);
		if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("data", out var data)
			|| data.ValueKind != JsonValueKind.Array)
		{
			return [];
		}
		var numbers = data.EnumerateArray().Select(v => v.GetInt32()).ToList();
		var tokens = new List<SemanticToken>(numbers.Count / 5);
		int line = 0, column = 0;
		for (int i = 0; i + 4 < numbers.Count; i += 5)
		{
			// Every token is relative to the one before it: same line means the column is
			// relative too, a new line resets it.
			line += numbers[i];
			column = numbers[i] == 0 ? column + numbers[i + 1] : numbers[i + 1];
			int length = numbers[i + 2];
			int type = numbers[i + 3];
			if (type < 0 || type >= tokenTypes.Length)
				continue;
			if (LspSymbolKinds.ClassificationOf(tokenTypes[type]) is { } classification)
				tokens.Add(new SemanticToken(line + 1, column + 1, length, classification));
		}
		return tokens;
	}

	/// <summary>
	/// A server classifies the file it holds, and it holds what it was sent. Text that is not
	/// that file - an older revision of it - is not something it can be asked about without
	/// telling it something untrue about the workspace, so this declines instead.
	/// </summary>
	public Task<IReadOnlyList<SemanticToken>> GetSemanticTokensForTextAsync(
		string relPath, string text, CancellationToken ct)
		=> string.Equals(Open(relPath)?.Text.ReplaceLineEndings("\n").TrimEnd('\n'),
			text.ReplaceLineEndings("\n").TrimEnd('\n'), StringComparison.Ordinal)
			? GetSemanticTokensAsync(relPath, ct)
			: Task.FromResult<IReadOnlyList<SemanticToken>>([]);

	public async Task<IReadOnlyList<ChangedMember>> MapLinesToMembersAsync(
		string relPath, IReadOnlyCollection<int> lines, CancellationToken ct)
	{
		var symbols = await DocumentSymbolsAsync(relPath, ct);
		var members = new List<ChangedMember>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (int line in lines.OrderBy(l => l))
		{
			if (Innermost(symbols, line) is not { } member || !seen.Add(member.Display))
				continue;
			members.Add(new ChangedMember(member.Display, LspSymbolKinds.NameOf(member.Kind), line));
		}
		return members;
	}

	public async Task<IReadOnlySet<string>> ListMemberDisplaysAsync(string relPath, CancellationToken ct)
	{
		var symbols = await DocumentSymbolsAsync(relPath, ct);
		return symbols.Select(s => s.Display).ToHashSet(StringComparer.Ordinal);
	}

	public async Task<IReadOnlyList<DeclarationHit>> FindDeclarationsAsync(
		string pattern, int max, CancellationToken ct)
	{
		if (pattern.Length == 0)
			return [];
		var result = await connection.RequestAsync("workspace/symbol", new { query = pattern }, ct);
		if (result.ValueKind != JsonValueKind.Array)
			return [];
		var hits = new List<DeclarationHit>();
		foreach (var symbol in result.EnumerateArray())
		{
			if (!symbol.TryGetProperty("location", out var location)
				|| !location.TryGetProperty("uri", out var uri)
				|| LspUri.ToPath(uri.GetString() ?? "") is not { } path
				|| ToRelativePath(path) is not { } rel)
			{
				continue;
			}
			var (line, _, _) = RangeOf(location.GetProperty("range"));
			hits.Add(new DeclarationHit(
				symbol.TryGetProperty("name", out var symbolName) ? symbolName.GetString() ?? "" : "",
				symbol.TryGetProperty("containerName", out var container) ? container.GetString() ?? "" : "",
				LspSymbolKinds.NameOf(symbol.TryGetProperty("kind", out var kind) ? kind.GetInt32() : 0),
				rel,
				line));
			if (hits.Count >= max)
				break;
		}
		return hits;
	}

	public async Task<IReadOnlyList<CallNode>> GetCallsAsync(
		SymbolRef symbol, CallDirection direction, CancellationToken ct)
	{
		Open(symbol.RelPath);
		var prepared = await connection.RequestAsync("textDocument/prepareCallHierarchy", new {
			textDocument = new { uri = LspUri.FromPath(ToAbsolutePath(symbol.RelPath)) },
			position = new { line = symbol.Line - 1, character = symbol.Column - 1 },
		}, ct);
		if (prepared.ValueKind != JsonValueKind.Array || prepared.GetArrayLength() == 0)
			return [];
		var item = prepared[0];
		bool callers = direction == CallDirection.Callers;
		var result = await connection.RequestAsync(
			callers ? "callHierarchy/incomingCalls" : "callHierarchy/outgoingCalls",
			new { item }, ct);
		if (result.ValueKind != JsonValueKind.Array)
			return [];
		var nodes = new List<CallNode>();
		foreach (var call in result.EnumerateArray())
		{
			string side = callers ? "from" : "to";
			if (!call.TryGetProperty(side, out var other))
				continue;
			string? path = other.TryGetProperty("uri", out var uri) ? LspUri.ToPath(uri.GetString() ?? "") : null;
			var (line, column, _) = other.TryGetProperty("selectionRange", out var selection)
				? RangeOf(selection)
				: (0, 0, 0);
			var sites = new List<CallSite>();
			if (call.TryGetProperty("fromRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
			{
				// Incoming calls report the sites inside the caller's file; outgoing ones
				// report them inside the file that was asked about.
				string sitePath = (callers ? path : ToAbsolutePath(symbol.RelPath)) ?? "";
				foreach (var range in ranges.EnumerateArray())
				{
					var (siteLine, _, _) = RangeOf(range);
					sites.Add(new CallSite(sitePath, siteLine, LineTextOf(sitePath, siteLine)));
				}
			}
			nodes.Add(new CallNode(
				other.TryGetProperty("name", out var callName) ? callName.GetString() ?? "" : "",
				other.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "",
				path, line, column, sites));
		}
		return nodes;
	}

	#endregion

	#region Document symbols

	/// <summary>One entry per declaration a file makes, flattened: the tree's shape only
	/// matters here for naming a member by its container.</summary>
	sealed record FlatSymbol(
		string Name, string Display, int Kind, int StartLine, int EndLine, int SelectionLine, int SelectionColumn);

	async Task<IReadOnlyList<FlatSymbol>> DocumentSymbolsAsync(string relPath, CancellationToken ct)
	{
		if (Open(relPath) is null)
			return [];
		var result = await connection.RequestAsync("textDocument/documentSymbol", new {
			textDocument = new { uri = LspUri.FromPath(ToAbsolutePath(relPath)) },
		}, ct);
		if (result.ValueKind != JsonValueKind.Array)
			return [];
		var flat = new List<FlatSymbol>();
		foreach (var symbol in result.EnumerateArray())
			Flatten(symbol, "", flat);
		return flat;
	}

	static void Flatten(JsonElement symbol, string container, List<FlatSymbol> into)
	{
		string name = symbol.TryGetProperty("name", out var symbolName) ? symbolName.GetString() ?? "" : "";
		int kind = symbol.TryGetProperty("kind", out var symbolKind) ? symbolKind.GetInt32() : 0;
		// A hierarchical answer carries ranges; the flat (SymbolInformation) shape carries a
		// location instead, and no children.
		var range = symbol.TryGetProperty("range", out var own) ? own
			: symbol.TryGetProperty("location", out var location) ? location.GetProperty("range")
			: default;
		if (range.ValueKind != JsonValueKind.Object)
			return;
		var (startLine, _, _) = RangeOf(range);
		int endLine = range.TryGetProperty("end", out var end) && end.TryGetProperty("line", out var endLineValue)
			? endLineValue.GetInt32() + 1
			: startLine;
		var (selectionLine, selectionColumn, _) = symbol.TryGetProperty("selectionRange", out var selection)
			? RangeOf(selection)
			: (startLine, 1, 0);
		string display = container.Length > 0 ? container + "." + name : name;
		into.Add(new FlatSymbol(name, display, kind, startLine, endLine, selectionLine, selectionColumn));
		if (symbol.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
		{
			foreach (var child in children.EnumerateArray())
				Flatten(child, display, into);
		}
	}

	/// <summary>The narrowest declaration containing a line - the member it belongs to rather
	/// than the type the member is in.</summary>
	static FlatSymbol? Innermost(IReadOnlyList<FlatSymbol> symbols, int line)
		=> symbols
			.Where(s => s.StartLine <= line && line <= s.EndLine)
			.OrderByDescending(s => s.StartLine)
			.ThenBy(s => s.EndLine)
			.FirstOrDefault();

	#endregion

	#region Ranges, locations, text

	static (int Line, int Column, int Length) RangeOf(JsonElement range)
	{
		if (!range.TryGetProperty("start", out var start))
			return (0, 0, 0);
		int line = start.GetProperty("line").GetInt32() + 1;
		int column = start.GetProperty("character").GetInt32() + 1;
		int length = 0;
		if (range.TryGetProperty("end", out var end)
			&& end.GetProperty("line").GetInt32() + 1 == line)
		{
			length = end.GetProperty("character").GetInt32() + 1 - column;
		}
		return (line, column, length);
	}

	/// <summary>Locations of a definition or references answer, which is one location, an
	/// array of them, or an array of links depending on the server.</summary>
	IEnumerable<SymbolLocation> Locations(JsonElement result)
	{
		if (result.ValueKind == JsonValueKind.Object)
		{
			if (ToLocation(result) is { } single)
				yield return single;
			yield break;
		}
		if (result.ValueKind != JsonValueKind.Array)
			yield break;
		foreach (var element in result.EnumerateArray())
		{
			if (ToLocation(element) is { } location)
				yield return location;
		}
	}

	SymbolLocation? ToLocation(JsonElement element)
	{
		string? uri = element.TryGetProperty("uri", out var own) ? own.GetString()
			: element.TryGetProperty("targetUri", out var target) ? target.GetString()
			: null;
		if (uri is null || LspUri.ToPath(uri) is not { } path)
			return null;
		var range = element.TryGetProperty("range", out var ownRange) ? ownRange
			: element.TryGetProperty("targetSelectionRange", out var targetRange) ? targetRange
			: default;
		if (range.ValueKind != JsonValueKind.Object)
			return null;
		var (line, column, length) = RangeOf(range);
		return new SymbolLocation(path, line, column, length);
	}

	/// <summary>The text of one line of a file, for the references list. Files the review
	/// never opened are read once and remembered with the rest.</summary>
	string LineTextOf(string absolutePath, int line)
	{
		if (ToRelativePath(absolutePath) is not { } rel)
			return "";
		if (!openDocuments.TryGetValue(rel, out var index))
		{
			if (ReadFile(rel) is not { } text)
				return "";
			// Read for its text only: telling the server about every file a search touched
			// would open half the repository.
			index = new TextIndex(text);
			openDocuments[rel] = index;
		}
		return index.LineText(line).Trim();
	}

	static string[] ReadTokenLegend(JsonElement capabilities)
	{
		if (capabilities.ValueKind != JsonValueKind.Object
			|| !capabilities.TryGetProperty("semanticTokensProvider", out var provider)
			|| !provider.TryGetProperty("legend", out var legend)
			|| !legend.TryGetProperty("tokenTypes", out var types)
			|| types.ValueKind != JsonValueKind.Array)
		{
			return [];
		}
		return [.. types.EnumerateArray().Select(t => t.GetString() ?? "")];
	}

	#endregion

	public void Dispose()
	{
		state = SemanticState.NotLoaded;
		StateChanged?.Invoke();
		connection.Dispose();
	}
}
