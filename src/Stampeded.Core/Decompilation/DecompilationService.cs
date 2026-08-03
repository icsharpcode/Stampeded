using System.IO;
using System.Reflection.Metadata.Ecma335;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace Stampeded.Core.Decompilation;

public sealed record DecompiledType(string Text, int MemberLine);

/// <summary>
/// Decompiles a metadata type to C# for symbols that have no source in the loaded
/// solution, and locates a member inside the output by metadata token.
/// </summary>
public static class DecompilationService
{
	/// <summary>Decompiles the top-level type named by <paramref name="reflectionName"/>
	/// (e.g. "System.Collections.Generic.List`1") from <paramref name="assemblyPath"/>.
	/// <paramref name="targetMetadataToken"/> selects the declaration whose line is
	/// reported (0, or an unknown token, reports line 1).</summary>
	public static DecompiledType DecompileType(string assemblyPath, string reflectionName, int targetMetadataToken)
	{
		var settings = new DecompilerSettings(LanguageVersion.Latest) {
			ThrowOnAssemblyResolveErrors = false,
		};
		var decompiler = new CSharpDecompiler(assemblyPath, settings);
		var tree = decompiler.DecompileType(new FullTypeName(reflectionName));
		var writer = new StringWriter();
		var locator = new MemberLocatingTokenWriter(TokenWriter.Create(writer), targetMetadataToken);
		tree.AcceptVisitor(new CSharpOutputVisitor(locator, FormattingOptionsFactory.CreateAllman()));
		return new DecompiledType(writer.ToString(), locator.FoundLine ?? 1);
	}

	/// <summary>Watches identifiers as the syntax tree is written and remembers the line of
	/// the declaration matching the target metadata token. Location tracking is a plain
	/// NewLine() count, so it needs no support from the underlying writer.</summary>
	sealed class MemberLocatingTokenWriter(TokenWriter inner, int targetToken) : DecoratingTokenWriter(inner)
	{
		int currentLine = 1;
		int? identifierLine;
		int? declarationLine;

		// The identifier position is exact but some declarations write no Identifier node
		// (indexers, operators); their StartNode line (which may point at leading
		// attributes) is the fallback.
		public int? FoundLine => identifierLine ?? declarationLine;

		public override void NewLine()
		{
			currentLine++;
			base.NewLine();
		}

		public override void StartNode(AstNode node)
		{
			if (declarationLine is null && node is EntityDeclaration && Matches(node))
				declarationLine = currentLine;
			base.StartNode(node);
		}

		public override void WriteIdentifier(Identifier identifier)
		{
			if (identifierLine is null)
			{
				var node = identifier.Parent;
				// Field and event declarations put the name inside a VariableInitializer.
				if (node is VariableInitializer)
					node = node.Parent;
				if (node is EntityDeclaration && Matches(node))
					identifierLine = currentLine;
			}
			base.WriteIdentifier(identifier);
		}

		bool Matches(AstNode declaration)
			=> declaration.GetSymbol() is IEntity entity
				&& !entity.MetadataToken.IsNil
				&& MetadataTokens.GetToken(entity.MetadataToken) == targetToken;
	}
}
