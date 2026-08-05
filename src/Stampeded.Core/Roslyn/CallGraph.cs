namespace Stampeded.Core.Roslyn;

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
