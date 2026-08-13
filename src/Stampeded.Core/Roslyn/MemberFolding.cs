using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Stampeded.Core.Roslyn;

/// <summary>A foldable member region, 1-based inclusive source lines.</summary>
public sealed record MemberFoldRegion(int StartLine, int EndLine);

/// <summary>
/// Computes IDE-style folding regions (types, methods and friends, properties,
/// indexers, events, #region blocks) from C# source via a syntax-only parse.
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
		AddRegionDirectives(tree, text, regions);
		return regions;
	}

	/// <summary>
	/// Folds each #region to its #endregion. The "#region Name" line stays visible - the fold
	/// starts at its end - so the collapsed block still says what it is and needs no label of
	/// its own.
	///
	/// Unlike declarations, these are not bound by the syntax tree's nesting: a #region may
	/// open inside a member and close outside it. Such a fold crosses another one, which no
	/// folding manager can represent, so it is left out rather than allowed to corrupt the
	/// set it is added to.
	/// </summary>
	static void AddRegionDirectives(SyntaxTree tree, SourceText text, List<MemberFoldRegion> regions)
	{
		foreach (var directive in tree.GetRoot().DescendantTrivia()
			.Where(t => t.IsKind(SyntaxKind.RegionDirectiveTrivia))
			.Select(t => t.GetStructure())
			.OfType<RegionDirectiveTriviaSyntax>())
		{
			var related = directive.GetRelatedDirectives();
			// An unclosed #region has no end to fold to.
			if (related.Count < 2 || related[^1] is not EndRegionDirectiveTriviaSyntax end)
				continue;
			int start = text.Lines.GetLinePosition(directive.SpanStart).Line + 1;
			int endLine = text.Lines.GetLinePosition(end.Span.End).Line + 1;
			if (endLine <= start || regions.Any(r => Crosses(r, start, endLine)))
				continue;
			regions.Add(new MemberFoldRegion(start, endLine));
		}

		// Nested or disjoint is fine; overlapping without containment is not.
		static bool Crosses(MemberFoldRegion other, int start, int end)
			=> (start > other.StartLine && start <= other.EndLine && end > other.EndLine)
				|| (other.StartLine > start && other.StartLine <= end && other.EndLine > end);
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
