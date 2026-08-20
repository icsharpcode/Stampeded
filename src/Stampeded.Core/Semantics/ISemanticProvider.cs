namespace Stampeded.Core.Semantics;

/// <summary>
/// Everything the review asks about source it did not write: what a token means, where it is
/// declared, who uses it, which member a line belongs to.
///
/// One instance serves one side of one review - head or merge base - for one language. The
/// shape is deliberately what a language server can answer: symbols are positions
/// (<see cref="SymbolRef"/>), every call is asynchronous and cancellable, and nothing hands
/// out a compiler's own objects. An in-process Roslyn implementation loses nothing by it,
/// and an out-of-process one becomes possible.
/// </summary>
public interface ISemanticProvider : IDisposable
{
	/// <summary>How much can be answered right now. The commands that need a loaded
	/// compilation are offered only from <see cref="SemanticState.Ready"/> or
	/// <see cref="SemanticState.SyntaxOnly"/>.</summary>
	SemanticState State { get; }

	/// <summary>One line for the status bar: which solution, which failure, how far.</summary>
	string StateDetail { get; }

	/// <summary>The whole load transcript, for the Log pane.</summary>
	string LoadLog { get; }

	event Action? StateChanged;

	/// <summary>The repository-relative form of a path this provider reported, or null when
	/// the file is outside the tree it serves.</summary>
	string? ToRelativePath(string absolutePath);

	string ToAbsolutePath(string repoRelativePath);

	/// <summary>
	/// Makes the given files read as the supplied text instead of what is on disk, so a
	/// review of a scope answers about the scope. Files not named keep their own content.
	/// </summary>
	void SetTextOverlay(IReadOnlyDictionary<string, string> textByRelativePath);

	void ClearTextOverlay();

	/// <summary>The text this provider currently has for a file, for checking that its
	/// answers describe what is on screen; null when it has no such file.</summary>
	Task<string?> GetDocumentTextAsync(string relPath, CancellationToken ct);

	/// <summary>Absolute offset of a 1-based (line, column), the coordinate the position
	/// arguments below take.</summary>
	Task<int?> GetPositionAsync(string relPath, int line, int column, CancellationToken ct);

	Task<IReadOnlyList<SemanticToken>> GetSemanticTokensAsync(string relPath, CancellationToken ct);

	/// <summary>Tokens for text that is not what the provider has loaded - a historical
	/// revision of a file, say - classified on its own.</summary>
	Task<IReadOnlyList<SemanticToken>> GetSemanticTokensForTextAsync(
		string relPath, string text, CancellationToken ct);

	/// <summary>One line about the symbol at a position, for the status bar.</summary>
	Task<string?> GetQuickInfoAsync(string relPath, int position, CancellationToken ct);

	/// <summary>The full hover text, documentation included.</summary>
	Task<string?> GetHoverTextAsync(string relPath, int position, CancellationToken ct);

	Task<SymbolRef?> GetSymbolAtAsync(string relPath, int position, CancellationToken ct);

	/// <summary>The symbol at a column, falling back to any identifier on the same line: a
	/// caret sits wherever it was left, often in the indentation.</summary>
	Task<SymbolRef?> GetSymbolOnLineAsync(string relPath, int line, int preferredColumn, CancellationToken ct);

	/// <summary>The member a line is inside, rather than the token on it - which on a body
	/// line is a local or a callee, and says nothing about what changed.</summary>
	Task<SymbolRef?> GetEnclosingMemberAsync(string relPath, int line, CancellationToken ct);

	/// <summary>Where the symbol is declared, or null when it has no source (metadata,
	/// a generated file the provider does not hold).</summary>
	Task<SymbolLocation?> GetDefinitionAsync(SymbolRef symbol, CancellationToken ct);

	Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(SymbolRef symbol, CancellationToken ct);

	/// <summary>Uses of the symbol within one file, for highlighting occurrences.</summary>
	Task<IReadOnlyList<SemanticToken>> FindOccurrencesInFileAsync(
		SymbolRef symbol, string relPath, CancellationToken ct);

	Task<IReadOnlyList<CallNode>> GetCallsAsync(SymbolRef symbol, CallDirection direction, CancellationToken ct);

	/// <summary>Declarations whose name matches a pattern, for going to one by name.</summary>
	Task<IReadOnlyList<DeclarationHit>> FindDeclarationsAsync(string pattern, int max, CancellationToken ct);

	/// <summary>The distinct members containing the given 1-based lines of a file.</summary>
	Task<IReadOnlyList<ChangedMember>> MapLinesToMembersAsync(
		string relPath, IReadOnlyCollection<int> lines, CancellationToken ct);

	/// <summary>Every member a file declares, named as <see cref="ChangedMember.Display"/>
	/// names them, for telling a moved member from a deleted one.</summary>
	Task<IReadOnlySet<string>> ListMemberDisplaysAsync(string relPath, CancellationToken ct);
}

/// <summary>
/// A provider that can say which assembly a symbol without source came from, so the definition
/// can be decompiled instead of "not found". Only a provider with real metadata behind it can:
/// a language server answers about files, and there is no file.
/// </summary>
public interface IDecompileTargets
{
	Task<DecompileTarget?> GetDecompileTargetAsync(SymbolRef symbol, CancellationToken ct);
}
