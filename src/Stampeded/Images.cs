// Copyright (c) 2026 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using Avalonia.Media;
using Avalonia.Svg.Skia;

namespace Stampeded;

/// <summary>
/// Tree-node icons, taken from ILSpy's icon set (see Assets/Icons/README.md for their
/// origin and license). Loaded once; SvgImage instances are freely shareable.
/// </summary>
public static class Images
{
	const string AssetBase = "avares://Stampeded/Assets/Icons/";

	static IImage LoadSvg(string name)
	{
		return new SvgImage {
			Source = SvgSource.Load(AssetBase + name + ".svg", null)
		};
	}

	public static readonly IImage Assembly = LoadSvg(nameof(Assembly));
	public static readonly IImage Class = LoadSvg(nameof(Class));
	public static readonly IImage Struct = LoadSvg(nameof(Struct));
	public static readonly IImage Interface = LoadSvg(nameof(Interface));
	public static readonly IImage Enum = LoadSvg(nameof(Enum));
	public static readonly IImage Delegate = LoadSvg(nameof(Delegate));
	public static readonly IImage Method = LoadSvg(nameof(Method));
	public static readonly IImage Constructor = LoadSvg(nameof(Constructor));
	public static readonly IImage Operator = LoadSvg(nameof(Operator));
	public static readonly IImage Property = LoadSvg(nameof(Property));
	public static readonly IImage Indexer = LoadSvg(nameof(Indexer));
	public static readonly IImage Event = LoadSvg(nameof(Event));
	public static readonly IImage Field = LoadSvg(nameof(Field));
	public static readonly IImage FolderClosed = LoadSvg(nameof(FolderClosed));
	public static readonly IImage FolderOpen = LoadSvg(nameof(FolderOpen));
	public static readonly IImage Document = LoadSvg("Resource");
	public static readonly IImage ViewCode = LoadSvg(nameof(ViewCode));
	public static readonly IImage SubTypes = LoadSvg(nameof(SubTypes));
	public static readonly IImage SuperTypes = LoadSvg(nameof(SuperTypes));

	// The toolbar commands. Same source and the same treatment as the tree icons above: an SVG
	// at 16 by 16, drawn at whatever size the button asks for.
	public static readonly IImage Commit = LoadSvg(nameof(Commit));
	public static readonly IImage History = LoadSvg(nameof(History));
	public static readonly IImage Diff = LoadSvg(nameof(Diff));
	public static readonly IImage Previous = LoadSvg(nameof(Previous));
	public static readonly IImage Next = LoadSvg(nameof(Next));
	public static readonly IImage VisualStudioCode = LoadSvg(nameof(VisualStudioCode));
	public static readonly IImage GitHub = LoadSvg(nameof(GitHub));
	public static readonly IImage Checkmark = LoadSvg(nameof(Checkmark));
	public static readonly IImage Refresh = LoadSvg(nameof(Refresh));
	public static readonly IImage Close = LoadSvg(nameof(Close));
	public static readonly IImage RunTest = LoadSvg(nameof(RunTest));
	public static readonly IImage CodeCoverage = LoadSvg(nameof(CodeCoverage));
	public static readonly IImage CompareFiles = LoadSvg(nameof(CompareFiles));
	public static readonly IImage Filter = LoadSvg(nameof(Filter));
	public static readonly IImage Clear = LoadSvg("ClearWindowContent");
	public static readonly IImage Run = LoadSvg(nameof(Run));
	public static readonly IImage Fetch = LoadSvg(nameof(Fetch));
	public static readonly IImage Pull = LoadSvg(nameof(Pull));
	public static readonly IImage Push = LoadSvg(nameof(Push));
	public static readonly IImage Rebase = LoadSvg(nameof(Rebase));
	public static readonly IImage Branch = LoadSvg(nameof(Branch));
	public static readonly IImage Delete = LoadSvg(nameof(Delete));
	public static readonly IImage FolderOpened = LoadSvg(nameof(FolderOpened));
	public static readonly IImage OpenWebSite = LoadSvg(nameof(OpenWebSite));
	public static readonly IImage OpenFolder = LoadSvg(nameof(OpenFolder));
	public static readonly IImage Comment = LoadSvg(nameof(Comment));
	public static readonly IImage Merge = LoadSvg(nameof(Merge));
	public static readonly IImage Cancel = LoadSvg(nameof(Cancel));

	// What a file is, for the tree that lists them. Same treatment as the icons above: a 16x16
	// SVG drawn at whatever size the row asks for.
	public static readonly IImage FileCSharp = LoadSvg("CSFileNode");
	public static readonly IImage FileVisualBasic = LoadSvg("VBFileNode");
	public static readonly IImage FileFSharp = LoadSvg("FSFileNode");
	// A project file is shown as its language's project node, which is the same glyph as the
	// language's source files in a box. The generic "Project" image is a badge of its own that
	// looks like nothing else in the tree, and a .csproj is not a different kind of thing from
	// the .cs files under it.
	public static readonly IImage FileCSharpProject = LoadSvg("CSProjectNode");
	public static readonly IImage FileVisualBasicProject = LoadSvg("VBProjectNode");
	public static readonly IImage FileFSharpProject = LoadSvg("FSProjectNode");
	public static readonly IImage FileProject = LoadSvg("Project");
	public static readonly IImage FileSolution = LoadSvg("Solution");
	public static readonly IImage FileXml = LoadSvg("XmlFile");
	public static readonly IImage FileXaml = LoadSvg("WPFFile");
	public static readonly IImage FileJson = LoadSvg("JSONScript");
	public static readonly IImage FileMarkdown = LoadSvg("MarkdownFile");
	public static readonly IImage FileText = LoadSvg("TextFile");
	public static readonly IImage FileYaml = LoadSvg("YamlFile");
	public static readonly IImage FileHtml = LoadSvg("HTMLFile");
	public static readonly IImage FileScript = LoadSvg("JSScript");
	public static readonly IImage FileStyleSheet = LoadSvg("StyleSheet");
	public static readonly IImage FilePowerShell = LoadSvg("PowershellFile");
	public static readonly IImage FileShell = LoadSvg("Console");
	public static readonly IImage FileImage = LoadSvg("Image");
	public static readonly IImage FileCertificate = LoadSvg("Certificate");
	public static readonly IImage FileDatabase = LoadSvg("Database");
	public static readonly IImage FileLock = LoadSvg("Lock");
	public static readonly IImage FileGit = LoadSvg("Git");
	public static readonly IImage FileBinary = LoadSvg("BinaryFile");
	public static readonly IImage FileSettings = LoadSvg("Settings");

	/// <summary>
	/// What a file is, from its name. A directory listing is a wall of identical rows without
	/// this, and the kind of a file is the first thing a reader sorts by - so it is worth
	/// reading off the one thing every row already carries. Anything unrecognised keeps the
	/// plain document icon rather than being guessed at.
	/// </summary>
	public static IImage ForFileName(string fileName)
	{
		string name = fileName.ToLowerInvariant();
		// Whole names first: a dot-file carries its kind where another file carries an
		// extension, and asking for the extension of ".gitignore" is asking the wrong question.
		return name switch {
			".gitignore" or ".gitattributes" or ".gitmodules" or ".mailmap" => FileGit,
			".editorconfig" => FileSettings,
			"dockerfile" or "makefile" => FileText,
			_ => ForExtension(System.IO.Path.GetExtension(name)),
		};
	}

	static IImage ForExtension(string extension) => extension switch {
		".cs" or ".csx" => FileCSharp,
		".vb" => FileVisualBasic,
		".fs" or ".fsi" or ".fsx" => FileFSharp,
		".csproj" => FileCSharpProject,
		".vbproj" => FileVisualBasicProject,
		".fsproj" => FileFSharpProject,
		// A project of no particular language - a build-only project, a shared one - keeps the
		// generic badge, which is the one place it says something.
		".proj" or ".shproj" or ".projitems" => FileProject,
		".sln" or ".slnx" or ".slnf" => FileSolution,
		".xml" or ".config" or ".props" or ".targets" or ".nuspec" or ".resx" or ".xsd"
			or ".xslt" or ".vsixmanifest" or ".xshd" or ".ruleset" => FileXml,
		".xaml" or ".axaml" => FileXaml,
		".json" or ".jsonc" => FileJson,
		".md" or ".markdown" => FileMarkdown,
		".txt" or ".log" or ".rtf" or ".csv" => FileText,
		".yml" or ".yaml" => FileYaml,
		".html" or ".htm" or ".xhtml" or ".cshtml" or ".razor" => FileHtml,
		// TypeScript has no icon of its own in the set; it is the same kind of file as its
		// output, and a reader looking for a script finds one.
		".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".tsx" => FileScript,
		".css" or ".scss" or ".less" => FileStyleSheet,
		".ps1" or ".psm1" or ".psd1" => FilePowerShell,
		".sh" or ".bash" or ".bat" or ".cmd" => FileShell,
		".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".svg" or ".webp" => FileImage,
		".pfx" or ".cer" or ".crt" or ".pem" or ".p12" or ".snk" => FileCertificate,
		".db" or ".sqlite" or ".sqlite3" or ".mdf" => FileDatabase,
		".lock" => FileLock,
		".dll" or ".exe" or ".so" or ".dylib" => Assembly,
		".bin" or ".dat" or ".pdb" or ".zip" or ".nupkg" => FileBinary,
		".il" => ViewCode,
		_ => Document,
	};

	/// <summary>Icon for a DocumentOutline node kind (lowercase syntax kinds).</summary>
	public static IImage? ForOutlineKind(string kind) => kind switch {
		"class" or "record" => Class,
		"struct" => Struct,
		"interface" => Interface,
		"enum" => Enum,
		"delegate" => Delegate,
		"method" => Method,
		"ctor" => Constructor,
		"operator" => Operator,
		"property" => Property,
		"indexer" => Indexer,
		"event" => Event,
		"field" => Field,
		_ => null,
	};

	/// <summary>Icon for a change-map member kind (Roslyn TypeKind/SymbolKind names).</summary>
	public static IImage? ForMemberKind(string kind) => kind switch {
		"Class" or "Error" => Class,
		"Struct" => Struct,
		"Interface" => Interface,
		"Enum" => Enum,
		"Delegate" => Delegate,
		"Method" => Method,
		"Constructor" => Constructor,
		"Operator" => Operator,
		"Property" => Property,
		"Indexer" => Indexer,
		"Event" => Event,
		"Field" => Field,
		_ => null,
	};
}
