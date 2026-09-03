using System.Diagnostics;
using System.Text.Json;

using Stampeded.Core.Infra;
using Stampeded.Core.Lsp;
using Stampeded.Core.Roslyn;

namespace Stampeded.RoslynLsp;

/// <summary>
/// The request loop. One workspace per side of the review: the head one is loaded from the
/// worktree named at <c>initialize</c>, and the base one is derived from it when the client
/// sends the texts of the revision it is comparing against - deriving is what makes the base
/// side affordable, since a second design-time build of the same worktree would cost minutes
/// to arrive at the answers the first one already has.
///
/// A document URI carries which side it is about: <c>?side=base</c> means the base workspace.
/// It is a query on a file URI rather than a scheme of its own so that everything which
/// merely wants the path - this server, the client, a log line - still reads one.
/// </summary>
sealed class RoslynLspServer : IDisposable
{
	/// <summary>A null result is an answer - "no definition here" - and JSON-RPC wants it
	/// written out, not dropped: a response with neither result nor error is malformed.</summary>
	static readonly JsonSerializerOptions Json = new() {
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	readonly Stream input;
	readonly Stream output;
	readonly SemaphoreSlim writeLock = new(1, 1);
	readonly RoslynWorkspaceService head = new();
	RoslynWorkspaceService? @base;
	Task headLoad = Task.CompletedTask;
	string rootPath = "";
	bool shuttingDown;

	public RoslynLspServer(Stream input, Stream output)
	{
		this.input = input;
		this.output = output;
	}

	public async Task RunAsync(CancellationToken ct)
	{
		// Nothing is added to CliLog's sink: everything written for a human already goes to
		// stderr, which the client copies into its Log pane. A sink as well would put every
		// line there twice.
		while (!shuttingDown)
		{
			if (await LspStream.ReadMessageAsync(input, ct) is not { } payload)
				break;
			var message = JsonDocument.Parse(payload).RootElement;
			if (!message.TryGetProperty("method", out var methodName))
				continue;
			string method = methodName.GetString() ?? "";
			var parameters = message.TryGetProperty("params", out var p) ? p : default;
			if (!message.TryGetProperty("id", out var id))
			{
				await HandleNotificationAsync(method, parameters, ct);
				continue;
			}
			// Serially, in the order they arrived: Roslyn answers are cheap once the
			// solution is loaded, and a review asks one question at a time.
			try
			{
				await RespondAsync(id, await HandleRequestAsync(method, parameters, ct));
			}
			catch (Exception ex)
			{
				CliLog.Write("roslyn-lsp", $"{method} FAILED: {ex.Message}");
				await RespondAsync(id, null);
			}
		}
	}

	#region Dispatch

	async Task<object?> HandleRequestAsync(string method, JsonElement parameters, CancellationToken ct) => method switch {
		"initialize" => await InitializeAsync(parameters, ct),
		"shutdown" => Shutdown(),
		"textDocument/definition" => await DefinitionAsync(parameters, ct),
		"textDocument/references" => await ReferencesAsync(parameters, ct),
		"textDocument/hover" => await HoverAsync(parameters, ct),
		"textDocument/documentHighlight" => await HighlightAsync(parameters, ct),
		"textDocument/documentSymbol" => await DocumentSymbolAsync(parameters, ct),
		"textDocument/semanticTokens/full" => await SemanticTokensAsync(parameters, ct),
		"textDocument/prepareCallHierarchy" => await PrepareCallHierarchyAsync(parameters, ct),
		"callHierarchy/incomingCalls" => await CallsAsync(parameters, CallDirection.Callers, ct),
		"callHierarchy/outgoingCalls" => await CallsAsync(parameters, CallDirection.Callees, ct),
		"workspace/symbol" => await WorkspaceSymbolAsync(parameters, ct),
		"stampeded/loadBase" => await LoadBaseAsync(parameters, ct),
		"stampeded/decompileTarget" => await DecompileTargetAsync(parameters, ct),
		"stampeded/changedMembers" => await ChangedMembersAsync(parameters, ct),
		"stampeded/memberDisplays" => await MemberDisplaysAsync(parameters, ct),
		_ => null,
	};

	async Task HandleNotificationAsync(string method, JsonElement parameters, CancellationToken ct)
	{
		switch (method)
		{
			case "exit":
				shuttingDown = true;
				break;
			case "textDocument/didOpen":
			case "textDocument/didChange":
				// The client sends what the review is showing, which may be a revision that
				// is not on disk. An overlay is how the workspace is told.
				await OverlayAsync(parameters, ct);
				break;
			case "initialized":
			case "$/cancelRequest":
				break;
			default:
				await Task.CompletedTask;
				break;
		}
	}

	object? Shutdown()
	{
		shuttingDown = true;
		return null;
	}

	#endregion

	#region Loading

	async Task<object> InitializeAsync(JsonElement parameters, CancellationToken ct)
	{
		WatchParent(parameters);
		rootPath = parameters.TryGetProperty("rootUri", out var uri)
			&& LspUri.ToPath(uri.GetString() ?? "") is { } path
			? path
			: Directory.GetCurrentDirectory();
		string? solution = parameters.TryGetProperty("initializationOptions", out var options)
			&& options.ValueKind == JsonValueKind.Object
			&& options.TryGetProperty("solution", out var chosen)
			? chosen.GetString()
			: null;
		head.StateChanged += ReportState;
		// Loading is minutes on a large solution, and initialize has to answer now: the
		// client is told the state as it changes and offers the commands that need a
		// compilation only once there is one.
		headLoad = Task.Run(() => head.LoadAsync(rootPath, solution, ct), ct);
		return new {
			capabilities = new {
				textDocumentSync = 1, // full text on every change
				definitionProvider = true,
				referencesProvider = true,
				hoverProvider = true,
				documentHighlightProvider = true,
				documentSymbolProvider = true,
				workspaceSymbolProvider = true,
				callHierarchyProvider = true,
				semanticTokensProvider = new {
					legend = new { tokenTypes = SemanticTokenLegend, tokenModifiers = Array.Empty<string>() },
					full = true,
				},
				// What this server has beyond the protocol, for a client that knows to ask.
				experimental = new {
					decompileTarget = true,
					derivedBaseSide = true,
					changedMembers = true,
					// initialize answers before the solution is loaded, so the client has to
					// wait for stampeded/state rather than assume it can ask anything yet.
					loadsAsynchronously = true,
				},
			},
			serverInfo = new { name = "Stampeded Roslyn", version = "1" },
		};
	}

	/// <summary>
	/// Ends this process when the client that asked for it is gone. The protocol sends the
	/// client's process id at initialize for exactly this: a client killed with a signal
	/// never gets to send shutdown, and a language server nobody can see is a solution's
	/// worth of memory left behind.
	/// </summary>
	void WatchParent(JsonElement parameters)
	{
		if (!parameters.TryGetProperty("processId", out var reported)
			|| !reported.TryGetInt32(out int processId))
		{
			return;
		}
		_ = Task.Run(async () => {
			while (!shuttingDown)
			{
				await Task.Delay(TimeSpan.FromSeconds(3));
				try
				{
					using var parent = Process.GetProcessById(processId);
					if (!parent.HasExited)
						continue;
				}
				catch (ArgumentException)
				{
					// No such process: it is gone, which is the answer we were waiting for.
				}
				CliLog.Write("roslyn-lsp", $"client {processId} is gone; stopping");
				Environment.Exit(0);
			}
		});
	}

	// An event handler cannot await, and the client only needs the notification to arrive
	// eventually: the write lock queues waiters in order, so states are still sent in the
	// order they were reached.
	void ReportState()
		=> NotifyAsync("stampeded/state", new { state = head.State.ToString(), detail = head.StateDetail })
			.ContinueWith(
				t => CliLog.Write("roslyn-lsp", $"state notification failed: {t.Exception?.GetBaseException().Message}"),
				TaskContinuationOptions.OnlyOnFaulted);

	/// <summary>Derives the base-side workspace from the head one, given the texts of the
	/// revision being compared against.</summary>
	async Task<object?> LoadBaseAsync(JsonElement parameters, CancellationToken ct)
	{
		// After the head has finished loading, whatever that costs: the base side is the
		// head's own compilation with some files reading differently, and deriving from a
		// solution that is still being opened produces a workspace that knows nothing.
		await headLoad;
		var replaced = ReadTexts(parameters, "replaced");
		var added = ReadTexts(parameters, "added");
		var removed = parameters.TryGetProperty("removed", out var removedPaths)
			&& removedPaths.ValueKind == JsonValueKind.Array
			? removedPaths.EnumerateArray().Select(e => e.GetString() ?? "").ToList()
			: [];
		var derived = new RoslynWorkspaceService();
		derived.LoadFrom(head, replaced, removed, added);
		@base?.Dispose();
		@base = derived;
		CliLog.Write("roslyn-lsp", $"base side: {replaced.Count} replaced, {removed.Count} removed, {added.Count} added");
		return new { ok = true };
	}

	static Dictionary<string, string> ReadTexts(JsonElement parameters, string name)
	{
		var texts = new Dictionary<string, string>(StringComparer.Ordinal);
		if (parameters.TryGetProperty(name, out var map) && map.ValueKind == JsonValueKind.Object)
		{
			foreach (var entry in map.EnumerateObject())
				texts[entry.Name] = entry.Value.GetString() ?? "";
		}
		return texts;
	}

	async Task OverlayAsync(JsonElement parameters, CancellationToken ct)
	{
		if (!parameters.TryGetProperty("textDocument", out var document)
			|| !document.TryGetProperty("uri", out var uri))
		{
			return;
		}
		string? text = parameters.TryGetProperty("text", out var direct)
			? direct.GetString()
			: parameters.TryGetProperty("contentChanges", out var changes)
				&& changes.ValueKind == JsonValueKind.Array && changes.GetArrayLength() > 0
				&& changes[0].TryGetProperty("text", out var changed)
				? changed.GetString()
				: null;
		if (text is null || Target(uri.GetString() ?? "") is not { } target || target.RelPath is null)
			return;
		// Only when it differs from what the workspace has: the client opens a document to
		// be able to ask about it at all, and re-stating the file on disk would throw away
		// the compilation that already knows it.
		if (await target.Service.GetDocumentTextAsync(target.RelPath, ct) is { } current
			&& string.Equals(current.ReplaceLineEndings("\n"), text.ReplaceLineEndings("\n"), StringComparison.Ordinal))
		{
			return;
		}
		target.Service.SetTextOverlay(new Dictionary<string, string> { [target.RelPath] = text });
	}

	#endregion

	#region Requests about a position

	/// <summary>Which workspace a URI is about, and the file within it.</summary>
	readonly record struct SideTarget(RoslynWorkspaceService Service, string? RelPath);

	SideTarget? Target(string uri)
	{
		if (LspUri.ToPath(uri) is not { } path)
			return null;
		bool baseSide = Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
			&& parsed.Query.Contains("side=base", StringComparison.Ordinal);
		var service = baseSide ? @base : head;
		if (service is null)
			return null;
		return new SideTarget(service, service.ToRelativePath(path));
	}

	/// <summary>The (service, file, offset) a positional request is about.</summary>
	async Task<(RoslynWorkspaceService Service, string RelPath, int Position)?> AtAsync(
		JsonElement parameters, CancellationToken ct)
	{
		if (!parameters.TryGetProperty("textDocument", out var document)
			|| !document.TryGetProperty("uri", out var uri)
			|| Target(uri.GetString() ?? "") is not { RelPath: { } relPath } target
			|| !parameters.TryGetProperty("position", out var position))
		{
			return null;
		}
		int line = position.GetProperty("line").GetInt32() + 1;
		int character = position.GetProperty("character").GetInt32() + 1;
		if (await target.Service.GetPositionAsync(relPath, line, character, ct) is not { } offset)
			return null;
		return (target.Service, relPath, offset);
	}

	async Task<object?> DefinitionAsync(JsonElement parameters, CancellationToken ct)
	{
		if (await AtAsync(parameters, ct) is not { } at)
			return null;
		var symbol = await at.Service.GetSymbolAtAsync(at.RelPath, at.Position, ct);
		if (symbol is null || await at.Service.GetDefinitionAsync(symbol, ct) is not { } location)
			return null;
		return LocationOf(location.FilePath, location.Line, location.Column, location.Length);
	}

	async Task<object?> ReferencesAsync(JsonElement parameters, CancellationToken ct)
	{
		if (await AtAsync(parameters, ct) is not { } at)
			return null;
		var symbol = await at.Service.GetSymbolAtAsync(at.RelPath, at.Position, ct);
		if (symbol is null)
			return Array.Empty<object>();
		var hits = await at.Service.FindReferencesAsync(symbol, ct);
		return hits.Select(h => LocationOf(h.FilePath, h.Line, h.Column, h.Length)).ToArray();
	}

	async Task<object?> HoverAsync(JsonElement parameters, CancellationToken ct)
	{
		if (await AtAsync(parameters, ct) is not { } at)
			return null;
		string? text = await at.Service.GetHoverTextAsync(at.RelPath, at.Position, ct);
		return text is null ? null : new { contents = new { kind = "plaintext", value = text } };
	}

	async Task<object?> HighlightAsync(JsonElement parameters, CancellationToken ct)
	{
		if (await AtAsync(parameters, ct) is not { } at)
			return Array.Empty<object>();
		var symbol = await at.Service.GetSymbolAtAsync(at.RelPath, at.Position, ct);
		if (symbol is null)
			return Array.Empty<object>();
		var occurrences = await at.Service.FindOccurrencesInFileAsync(symbol, at.RelPath, ct);
		return occurrences
			.Select(o => new {
				range = RangeOf(o.Line, o.Column, o.Length),
				kind = o.Classification == "definition" ? 3 : 2,
			})
			.ToArray();
	}

	async Task<object?> PrepareCallHierarchyAsync(JsonElement parameters, CancellationToken ct)
	{
		if (await AtAsync(parameters, ct) is not { } at)
			return Array.Empty<object>();
		var lineColumn = await LineColumnAsync(at.Service, at.RelPath, at.Position, ct);
		var symbol = lineColumn is { } position
			? await at.Service.GetSymbolOnLineAsync(at.RelPath, position.Line, position.Column, ct)
			: null;
		if (symbol is null)
			return Array.Empty<object>();
		// The item is the symbol's own position: an incoming/outgoing request re-resolves it,
		// so it has to name the member and not the call site that led here.
		return new[] {
			new {
				name = symbol.Display,
				kind = 12,
				uri = UriOf(at.Service, symbol.RelPath),
				range = RangeOf(symbol.Line, symbol.Column, symbol.Name.Length),
				selectionRange = RangeOf(symbol.Line, symbol.Column, symbol.Name.Length),
				detail = symbol.ContainingType?.Display ?? "",
			},
		};
	}

	async Task<object?> CallsAsync(JsonElement parameters, CallDirection direction, CancellationToken ct)
	{
		if (!parameters.TryGetProperty("item", out var item)
			|| !item.TryGetProperty("uri", out var uri)
			|| Target(uri.GetString() ?? "") is not { RelPath: { } relPath } target
			|| !item.TryGetProperty("selectionRange", out var range))
		{
			return Array.Empty<object>();
		}
		var start = range.GetProperty("start");
		var symbol = await target.Service.GetSymbolOnLineAsync(
			relPath, start.GetProperty("line").GetInt32() + 1, start.GetProperty("character").GetInt32() + 1, ct);
		if (symbol is null)
			return Array.Empty<object>();
		var nodes = await target.Service.GetCallsAsync(symbol, direction, ct);
		string side = direction == CallDirection.Callers ? "from" : "to";
		return nodes.Select(node => {
			var other = new {
				name = node.Display,
				kind = 12,
				uri = node.FilePath is null ? null : UriOf(target.Service, target.Service.ToRelativePath(node.FilePath) ?? ""),
				range = RangeOf(node.Line, node.Column, node.Display.Length),
				selectionRange = RangeOf(node.Line, node.Column, node.Display.Length),
				detail = node.ContainingType,
			};
			var ranges = node.Sites.Select(s => RangeOf(s.Line, 1, 0)).ToArray();
			return side == "from"
				? (object)new { from = other, fromRanges = ranges }
				: new { to = other, fromRanges = ranges };
		}).ToArray();
	}

	#endregion

	#region Requests about a file or the workspace

	async Task<object?> DocumentSymbolAsync(JsonElement parameters, CancellationToken ct)
	{
		if (!parameters.TryGetProperty("textDocument", out var document)
			|| !document.TryGetProperty("uri", out var uri)
			|| Target(uri.GetString() ?? "") is not { RelPath: { } relPath } target
			|| await target.Service.GetDocumentTextAsync(relPath, ct) is not { } text)
		{
			return Array.Empty<object>();
		}
		// The outline is a pure function of the text, so it answers about whatever revision
		// the client last sent, loaded solution or not.
		return DocumentOutline.Compute(text).Select(ToSymbol).ToArray();
	}

	static object ToSymbol(OutlineNode node) => new {
		name = NameOf(node),
		kind = SymbolKindOf(node.Kind),
		range = new {
			start = new { line = node.StartLine - 1, character = 0 },
			end = new { line = node.EndLine - 1, character = 0 },
		},
		selectionRange = RangeOf(node.StartLine, 1, node.Title.Length),
		children = node.Children.Select(ToSymbol).ToArray(),
	};

	/// <summary>
	/// The outline titles a type as the reader wants it in a tree - "class Greeter" - but a
	/// document symbol carries its kind in a field of its own, and the client builds a
	/// member's display by joining names with dots. Left in, the keyword would end up inside
	/// the display the change map compares against.
	/// </summary>
	static string NameOf(OutlineNode node)
		=> node.Title.StartsWith(node.Kind + " ", StringComparison.Ordinal)
			? node.Title[(node.Kind.Length + 1)..]
			: node.Title;

	/// <summary>The outline's kinds, which are C# keywords, as the protocol's SymbolKind
	/// numbers.</summary>
	static int SymbolKindOf(string kind) => kind switch {
		"class" or "record" => 5,
		"interface" => 11,
		"struct" => 23,
		"enum" => 10,
		"ctor" => 9,
		"property" or "indexer" => 7,
		"field" => 8,
		"event" => 24,
		"operator" or "method" => 6,
		// Also the shape a DeclarationHit reports, which names kinds the way Roslyn does.
		"Class" or "RecordClass" or "Delegate" => 5,
		"Interface" => 11,
		"Struct" or "RecordStruct" => 23,
		"Enum" => 10,
		"Constructor" => 9,
		"Property" or "Indexer" => 7,
		"Field" => 8,
		"Event" => 24,
		_ => 6,
	};

	async Task<object?> SemanticTokensAsync(JsonElement parameters, CancellationToken ct)
	{
		if (!parameters.TryGetProperty("textDocument", out var document)
			|| !document.TryGetProperty("uri", out var uri)
			|| Target(uri.GetString() ?? "") is not { RelPath: { } relPath } target)
		{
			return new { data = Array.Empty<int>() };
		}
		var tokens = await target.Service.GetSemanticTokensAsync(relPath, ct);
		var data = new List<int>(tokens.Count * 5);
		int lastLine = 0, lastColumn = 0;
		foreach (var token in tokens.OrderBy(t => t.Line).ThenBy(t => t.Column))
		{
			int type = Array.IndexOf(SemanticTokenLegend, LegendNameOf(token.Classification));
			if (type < 0)
				continue;
			int line = token.Line - 1, column = token.Column - 1;
			data.Add(line - lastLine);
			data.Add(line == lastLine ? column - lastColumn : column);
			data.Add(token.Length);
			data.Add(type);
			data.Add(0);
			lastLine = line;
			lastColumn = column;
		}
		return new { data };
	}

	/// <summary>The protocol's own token type names, in the order the legend fixes.</summary>
	static readonly string[] SemanticTokenLegend = [
		"class", "struct", "interface", "enum", "typeParameter", "method", "property",
		"field", "variable", "parameter", "enumMember", "event",
	];

	/// <summary>Roslyn's classification names as the protocol's token types; anything else
	/// is dropped, which is what the client would do with it anyway.</summary>
	static string LegendNameOf(string classification) => classification switch {
		"class name" or "record class name" or "delegate name" => "class",
		"struct name" or "record struct name" => "struct",
		"interface name" => "interface",
		"enum name" => "enum",
		"type parameter name" => "typeParameter",
		"method name" or "extension method name" => "method",
		"property name" => "property",
		"field name" => "field",
		"local name" => "variable",
		"parameter name" => "parameter",
		"enum member name" or "constant name" => "enumMember",
		"event name" => "event",
		_ => "",
	};

	async Task<object?> WorkspaceSymbolAsync(JsonElement parameters, CancellationToken ct)
	{
		string query = parameters.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
		var hits = await head.FindDeclarationsAsync(query, 100, ct);
		return hits.Select(h => new {
			name = h.Name,
			kind = SymbolKindOf(h.Kind),
			containerName = h.Container,
			location = new { uri = UriOf(head, h.RelPath), range = RangeOf(h.Line, 1, h.Name.Length) },
		}).ToArray();
	}

	async Task<object?> ChangedMembersAsync(JsonElement parameters, CancellationToken ct)
	{
		if (!parameters.TryGetProperty("uri", out var uri)
			|| Target(uri.GetString() ?? "") is not { RelPath: { } relPath } target
			|| !parameters.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
		{
			return Array.Empty<object>();
		}
		var members = await target.Service.MapLinesToMembersAsync(
			relPath, [.. lines.EnumerateArray().Select(l => l.GetInt32())], ct);
		return members.Select(m => new { display = m.Display, kind = m.Kind, firstLine = m.FirstLine }).ToArray();
	}

	async Task<object?> MemberDisplaysAsync(JsonElement parameters, CancellationToken ct)
	{
		if (!parameters.TryGetProperty("uri", out var uri)
			|| Target(uri.GetString() ?? "") is not { RelPath: { } relPath } target)
		{
			return Array.Empty<string>();
		}
		return (await target.Service.ListMemberDisplaysAsync(relPath, ct)).ToArray();
	}

	async Task<object?> DecompileTargetAsync(JsonElement parameters, CancellationToken ct)
	{
		if (await AtAsync(parameters, ct) is not { } at)
			return null;
		var symbol = await at.Service.GetSymbolAtAsync(at.RelPath, at.Position, ct);
		if (symbol is null || await at.Service.GetDecompileTargetAsync(symbol, ct) is not { } target)
			return null;
		return new {
			assemblyPath = target.AssemblyPath,
			reflectionName = target.ReflectionName,
			metadataToken = target.MetadataToken,
			typeName = target.TypeName,
		};
	}

	#endregion

	#region Wire

	static async Task<(int Line, int Column)?> LineColumnAsync(
		RoslynWorkspaceService service, string relPath, int position, CancellationToken ct)
	{
		if (await service.GetDocumentTextAsync(relPath, ct) is not { } text)
			return null;
		int line = 1, column = 1;
		for (int i = 0; i < position && i < text.Length; i++)
		{
			if (text[i] == '\n')
			{
				line++;
				column = 1;
			}
			else
			{
				column++;
			}
		}
		return (line, column);
	}

	string UriOf(RoslynWorkspaceService service, string relPath)
	{
		string uri = LspUri.FromPath(service.ToAbsolutePath(relPath));
		return ReferenceEquals(service, @base) ? uri + "?side=base" : uri;
	}

	static object LocationOf(string filePath, int line, int column, int length)
		=> new { uri = LspUri.FromPath(filePath), range = RangeOf(line, column, length) };

	static object RangeOf(int line, int column, int length) => new {
		start = new { line = line - 1, character = column - 1 },
		end = new { line = line - 1, character = column - 1 + Math.Max(0, length) },
	};

	async Task RespondAsync(JsonElement id, object? result)
	{
		var payload = JsonSerializer.SerializeToUtf8Bytes(
			new { jsonrpc = "2.0", id = id.Clone(), result }, Json);
		await SendAsync(payload);
	}

	Task NotifyAsync(string method, object parameters)
	{
		var payload = JsonSerializer.SerializeToUtf8Bytes(
			new { jsonrpc = "2.0", method, @params = parameters }, Json);
		return SendAsync(payload);
	}

	async Task SendAsync(byte[] payload)
	{
		await writeLock.WaitAsync();
		try
		{
			await LspStream.WriteMessageAsync(output, payload, CancellationToken.None);
		}
		finally
		{
			writeLock.Release();
		}
	}

	#endregion

	public void Dispose()
	{
		CliLog.Sink = null;
		@base?.Dispose();
		head.Dispose();
		writeLock.Dispose();
	}
}
