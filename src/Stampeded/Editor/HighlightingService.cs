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

using System.Xml;

using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

using Stampeded.Themes;

namespace Stampeded.Editor
{
	/// <summary>
	/// Extension-based lookup over AvaloniaEdit's built-in highlighting definitions (full
	/// C# keyword grammar, XML, ...), themed for dark mode on first use. ILSpy's trimmed
	/// CSharp-Mode.xshd is deliberately NOT used here: it omits keywords because ILSpy's
	/// decompiler colours them semantically, which a source viewer does not.
	/// </summary>
	static class HighlightingService
	{
		static HighlightingService()
		{
			// AvaloniaEdit ships no IL grammar, and IL is what a decompiler's own diffs are
			// full of. ILSpy's is vendored beside this file and registered once here, so a
			// .il file reads like one wherever the editor opens it.
			using var stream = typeof(HighlightingService).Assembly
				.GetManifestResourceStream("Stampeded.Editor.ILAsm-Mode.xshd");
			if (stream is null)
				return;
			using var reader = XmlReader.Create(stream);
			HighlightingManager.Instance.RegisterHighlighting(
				"ILAsm", [".il"], HighlightingLoader.Load(reader, HighlightingManager.Instance));
		}

		public static IHighlightingDefinition? GetByExtension(string fileExtension)
		{
			var definition = HighlightingManager.Instance.GetDefinitionByExtension(fileExtension);
			if (definition != null)
			{
				// Applies the active theme's colours on first use and re-themes on every
				// later switch. Registering is idempotent.
				ThemeManager.Current.RegisterThemableDefinition(definition);
			}
			return definition;
		}
	}
}
