namespace Stampeded.Core.Lsp;

/// <summary>
/// The two vocabularies a language server names things in - SymbolKind for declarations,
/// semantic token types for colouring - translated into the names the review already uses:
/// the kind names its icons are keyed on, and Roslyn's classification names, which are what
/// the editor's colour table understands whoever produced the token.
/// </summary>
static class LspSymbolKinds
{
	/// <summary>LSP SymbolKind, which is a number in the protocol and nothing else.</summary>
	static readonly string[] Names = [
		"", "File", "Module", "Namespace", "Package", "Class", "Method", "Property", "Field",
		"Constructor", "Enum", "Interface", "Function", "Variable", "Constant", "String",
		"Number", "Boolean", "Array", "Object", "Key", "Null", "EnumMember", "Struct",
		"Event", "Operator", "TypeParameter",
	];

	public static string NameOf(int kind) => kind > 0 && kind < Names.Length ? Names[kind] : "Unknown";

	public static bool IsType(int kind) => NameOf(kind) is "Class" or "Interface" or "Enum" or "Struct" or "Module";

	/// <summary>
	/// The classification a semantic token colours as, or null for a token type the review
	/// has no colour for - keywords, strings and comments among them, which the grammar
	/// already colours and which a second opinion would only fight with.
	/// </summary>
	public static string? ClassificationOf(string tokenType) => tokenType switch {
		"class" => "class name",
		"struct" => "struct name",
		"interface" => "interface name",
		"enum" => "enum name",
		"type" or "typeAlias" => "class name",
		"typeParameter" => "type parameter name",
		"method" or "function" => "method name",
		"property" => "property name",
		"field" => "field name",
		"variable" => "local name",
		"parameter" => "parameter name",
		"enumMember" => "enum member name",
		"event" => "event name",
		_ => null,
	};
}
