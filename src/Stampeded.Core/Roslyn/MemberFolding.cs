using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Stampeded.Core.Roslyn;

/// <summary>A foldable member region, 1-based inclusive source lines.</summary>
public sealed record MemberFoldRegion(int StartLine, int EndLine);

/// <summary>
/// Computes IDE-style folding regions (types, methods and friends, properties,
/// indexers, events) from C# source via a syntax-only parse.
/// </summary>
public static class MemberFolding
{
	public static IReadOnlyList<MemberFoldRegion> Compute(string source)
	{
		var tree = CSharpSyntaxTree.ParseText(source);
		var text = tree.GetText();
		var regions = new List<MemberFoldRegion>();
		foreach (var node in tree.GetRoot().DescendantNodes(descendIntoTrivia: false))
		{
			bool foldable = node is BaseTypeDeclarationSyntax
				or BaseMethodDeclarationSyntax
				or BasePropertyDeclarationSyntax
				or EventFieldDeclarationSyntax
				or LocalFunctionStatementSyntax;
			if (!foldable)
				continue;
			var span = text.Lines.GetLinePositionSpan(node.Span);
			int start = span.Start.Line + 1;
			int end = span.End.Line + 1;
			if (end > start)
				regions.Add(new MemberFoldRegion(start, end));
		}
		return regions;
	}
}
