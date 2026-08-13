using Microsoft.CodeAnalysis;
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
			int start = text.Lines.GetLinePosition(DeclarationStart(node)).Line + 1;
			int end = text.Lines.GetLinePosition(node.Span.End).Line + 1;
			if (end > start)
				regions.Add(new MemberFoldRegion(start, end));
		}
		return regions;
	}

	/// <summary>
	/// Where the declaration itself begins, past any attributes. Attributes are part of a
	/// member's span, so folding from there hides them - and they are what says what the
	/// member is for, which is the one thing a collapsed member cannot say for itself. A
	/// member that occupies a single line once its attributes are excluded stops folding
	/// altogether, which is right: there is nothing left to collapse.
	/// </summary>
	static int DeclarationStart(SyntaxNode node)
	{
		var attributes = node switch {
			MemberDeclarationSyntax member => member.AttributeLists,
			LocalFunctionStatementSyntax local => local.AttributeLists,
			_ => default,
		};
		return attributes.Count > 0
			? attributes[^1].GetLastToken().GetNextToken().SpanStart
			: node.SpanStart;
	}
}
