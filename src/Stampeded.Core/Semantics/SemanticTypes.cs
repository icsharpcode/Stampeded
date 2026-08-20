namespace Stampeded.Core.Semantics;

/// <summary>How much a provider can currently answer, and how the UI says so.</summary>
public enum SemanticState
{
	NotLoaded,
	Restoring,
	Loading,
	Ready,
	SyntaxOnly,
	Failed,
}

/// <summary>Where a symbol is declared. Absolute path, as the provider knows it; callers
/// map it back to a repository-relative one.</summary>
public sealed record SymbolLocation(string FilePath, int Line, int Column, int Length);

/// <summary>One place a symbol is used, with the line it sits on for the references list.</summary>
public sealed record ReferenceHit(string FilePath, int Line, int Column, int Length, string LineText);

/// <summary>A coloured range of one line, named by the classification the editor maps to a
/// colour. Line and column are 1-based, as everywhere the diff talks about positions.</summary>
public sealed record SemanticToken(int Line, int Column, int Length, string Classification);

/// <summary>A member the change touches, for the symbol-level change map.</summary>
public sealed record ChangedMember(string Display, string Kind, int FirstLine);

/// <summary>A declaration found by name: where it is, and what it is called in.</summary>
public sealed record DeclarationHit(string Name, string Container, string Kind, string RelPath, int Line);

public enum CallDirection
{
	/// <summary>Who calls this - the question a change asks: what breaks downstream.</summary>
	Callers,
	/// <summary>What this calls - how the member does its work.</summary>
	Callees,
}

/// <summary>Where one member actually calls another. A caller can call the same target
/// several times, and the reviewer's question is about the calls, not the signature.</summary>
public sealed record CallSite(string FilePath, int Line, string Preview);

/// <summary>
/// One member in a call hierarchy. The declaration position is what makes the tree
/// expandable: it is re-resolved to a symbol to find that member's own callers or callees.
/// A member without one (framework metadata, no source) is a leaf.
/// </summary>
public sealed record CallNode(
	string Display,
	string ContainingType,
	string? FilePath,
	int Line,
	int Column,
	IReadOnlyList<CallSite> Sites)
{
	public bool CanExpand => FilePath is { Length: > 0 };
}

/// <summary>
/// A symbol as a place rather than as an object: the file and 1-based position that resolves
/// to it, plus what it is called. Nothing else survives leaving a compiler's memory - a
/// language server names symbols by position and nothing more - so every provider can answer
/// about one of these, and a caller can hold one across a request without pinning a
/// compilation.
/// </summary>
/// <param name="Display">The symbol as an error message would name it, e.g.
/// <c>Foo.Bar(int)</c>. Compared against the change map, so it has to stay stable.</param>
/// <param name="ContainingType">The type declaring it, when that type has source of its own;
/// null for a type itself and for members whose type is only in metadata.</param>
public sealed record SymbolRef(
	string RelPath,
	int Line,
	int Column,
	string Display,
	string Name,
	bool IsType,
	SymbolRef? ContainingType);

/// <summary>A node of a document's structure outline; lines are 1-based source lines.</summary>
public sealed record OutlineNode(string Kind, string Title, int StartLine, int EndLine, IReadOnlyList<OutlineNode> Children);

/// <summary>A foldable member region, 1-based inclusive source lines.
/// <paramref name="HeaderEndLine"/> is the last line of the declaration itself - the one
/// carrying the "{" or "=>" that opens the body - which is where a signature stops being
/// readable on its own. It equals the start line for a single-line header, and for anything
/// whose declaration a provider cannot see inside.</summary>
public sealed record MemberFoldRegion(int StartLine, int EndLine, int HeaderEndLine);

/// <summary>What a symbol without source needs to be decompiled: the assembly it came from
/// and the type to read out of it.</summary>
public sealed record DecompileTarget(string AssemblyPath, string ReflectionName, int MetadataToken, string TypeName);
