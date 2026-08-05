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
