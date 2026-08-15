using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Stampeded.Core.Roslyn;

/// <summary>A foldable member region, 1-based inclusive source lines.
/// <paramref name="HeaderEndLine"/> is the last line of the declaration itself - the one
/// carrying the "{" or "=>" that opens the body - which is where a signature stops being
/// readable on its own. It equals the start line for a single-line header and for a
/// #region marker.</summary>
public sealed record MemberFoldRegion(int StartLine, int EndLine, int HeaderEndLine);

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
				regions.Add(new MemberFoldRegion(start, end, Math.Clamp(HeaderEnd(node, text, start), start, end)));
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
			regions.Add(new MemberFoldRegion(start, endLine, start));
		}

		// Nested or disjoint is fine; overlapping without containment is not.
		static bool Crosses(MemberFoldRegion other, int start, int end)
			=> (start > other.StartLine && start <= other.EndLine && end > other.EndLine)
				|| (other.StartLine > start && other.StartLine <= end && other.EndLine > end);
	}

	/// <summary>
	/// The line the declaration's own text ends on: the one carrying the "{" or "=>" that opens
	/// the body. A header runs over more than one line often enough - a wrapped parameter list,
	/// a base list, a where clause - and it is the whole of it that says what the code below
	/// belongs to, so half of it is worth no more than none. A declaration with no body at all
	/// (an abstract member, a record written with a semicolon, an event field) has no such
	/// token and ends where it starts.
	/// </summary>
	static int HeaderEnd(SyntaxNode node, SourceText text, int start)
	{
		// A type's brace is a token of its own; every other kind opens with a child node - a
		// block, an arrow clause, an accessor list - whose first token is the one wanted. Asking
		// the child nodes keeps an "=>" in a parameter default or a base list from being taken
		// for the one that opens the body.
		var token = node switch {
			BaseTypeDeclarationSyntax type => type.OpenBraceToken,
			_ => node.ChildNodes()
				.FirstOrDefault(c => c is BlockSyntax or ArrowExpressionClauseSyntax or AccessorListSyntax)
				?.GetFirstToken() ?? default,
		};
		return token.IsKind(SyntaxKind.None) ? start : text.Lines.GetLinePosition(token.SpanStart).Line + 1;
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
