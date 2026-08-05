using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Stampeded.Core.Roslyn;

/// <summary>A node of a document's structure outline; lines are 1-based source lines.</summary>
public sealed record OutlineNode(string Kind, string Title, int StartLine, int EndLine, IReadOnlyList<OutlineNode> Children);

/// <summary>
/// Computes an IDE-style document outline (types and their members) from C# source via
/// a syntax-only parse; resilient to broken code.
/// </summary>
public static class DocumentOutline
{
	public static IReadOnlyList<OutlineNode> Compute(string source)
	{
		var tree = CSharpSyntaxTree.ParseText(source);
		var text = tree.GetText();
		return OutlineMembers(tree.GetRoot() switch {
			CompilationUnitSyntax unit => unit.Members,
			var other => [.. other.ChildNodes().OfType<MemberDeclarationSyntax>()],
		}, text);
	}

	static IReadOnlyList<OutlineNode> OutlineMembers(IEnumerable<MemberDeclarationSyntax> members, SourceText text)
	{
		var nodes = new List<OutlineNode>();
		foreach (var member in members)
		{
			switch (member)
			{
				case BaseNamespaceDeclarationSyntax ns:
					// Namespaces are flattened; their types appear at this level.
					nodes.AddRange(OutlineMembers(ns.Members, text));
					break;
				case TypeDeclarationSyntax type:
					nodes.Add(Node("type", $"{type.Keyword.ValueText} {type.Identifier.ValueText}{type.TypeParameterList}",
						type, text, OutlineMembers(type.Members, text)));
					break;
				case EnumDeclarationSyntax enumDecl:
					nodes.Add(Node("type", $"enum {enumDecl.Identifier.ValueText}", enumDecl, text, []));
					break;
				case MethodDeclarationSyntax method:
					nodes.Add(Node("method", $"{method.Identifier.ValueText}({Parameters(method.ParameterList)})", method, text, []));
					break;
				case ConstructorDeclarationSyntax ctor:
					nodes.Add(Node("method", $"{ctor.Identifier.ValueText}({Parameters(ctor.ParameterList)})", ctor, text, []));
					break;
				case DestructorDeclarationSyntax dtor:
					nodes.Add(Node("method", $"~{dtor.Identifier.ValueText}()", dtor, text, []));
					break;
				case OperatorDeclarationSyntax op:
					nodes.Add(Node("method", $"operator {op.OperatorToken.ValueText}({Parameters(op.ParameterList)})", op, text, []));
					break;
				case ConversionOperatorDeclarationSyntax conv:
					nodes.Add(Node("method", $"{conv.ImplicitOrExplicitKeyword.ValueText} operator {conv.Type}", conv, text, []));
					break;
				case PropertyDeclarationSyntax property:
					nodes.Add(Node("property", property.Identifier.ValueText, property, text, []));
					break;
				case IndexerDeclarationSyntax indexer:
					nodes.Add(Node("property", $"this[{Parameters(indexer.ParameterList)}]", indexer, text, []));
					break;
				case EventDeclarationSyntax eventDecl:
					nodes.Add(Node("event", eventDecl.Identifier.ValueText, eventDecl, text, []));
					break;
				case EventFieldDeclarationSyntax eventField:
					foreach (var variable in eventField.Declaration.Variables)
						nodes.Add(Node("event", variable.Identifier.ValueText, eventField, text, []));
					break;
				case BaseFieldDeclarationSyntax field:
					foreach (var variable in field.Declaration.Variables)
						nodes.Add(Node("field", variable.Identifier.ValueText, field, text, []));
					break;
			}
		}
		return nodes;
	}

	static string Parameters(BaseParameterListSyntax? list)
		=> list is null ? "" : string.Join(", ", list.Parameters.Select(p => p.Type?.ToString() ?? p.Identifier.ValueText));

	static OutlineNode Node(string kind, string title, SyntaxNode syntax, SourceText text, IReadOnlyList<OutlineNode> children)
	{
		var span = text.Lines.GetLinePositionSpan(syntax.Span);
		return new OutlineNode(kind, title, span.Start.Line + 1, span.End.Line + 1, children);
	}
}
