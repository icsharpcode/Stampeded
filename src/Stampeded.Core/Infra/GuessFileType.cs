// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
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

using System.Text.Json;
using System.Xml;

namespace Stampeded.Core.Infra;

public enum FileType
{
	Text,
	Xml,
	Json,
}

/// <summary>
/// What a file holds, read from the content rather than from the name.
///
/// The XML half is ILSpy's <c>GuessFileType</c> (ICSharpCode.ILSpyX/Util/GuessFileType.cs): a
/// parser asked to move to the first content is the cheapest question that only well-formed XML
/// answers. Its encoding and binary detection is not here - every caller in this tool already
/// holds decoded text - and JSON is not there, so this pair is the two halves put together.
///
/// The point of asking at all is the file whose extension says nothing: .props, .targets,
/// .axaml, .slnx and .resx are XML that no highlighting definition claims, and .json is a
/// language nothing claims by name either.
/// </summary>
public static class GuessFileType
{
	/// <summary>
	/// What the text is, in as much as it parses as anything. Text is the answer for everything
	/// that is neither - a source file, prose, a log - because being unable to name a format is
	/// not evidence of one.
	/// </summary>
	public static FileType DetectTextType(string text)
	{
		if (LooksLikeXml(text))
			return FileType.Xml;
		return LooksLikeJson(text) ? FileType.Json : FileType.Text;
	}

	static bool LooksLikeXml(string text)
	{
		// A declaration, a comment or an element has to come first for this to be XML at all;
		// checking the character before building a parser keeps the common answer cheap.
		if (FirstContentChar(text) != '<')
			return false;
		try
		{
			var xmlReader = new XmlTextReader(new StringReader(text)) {
				// Nothing here is worth reaching the network or the disk for, and a DTD
				// reference in a file under review must not be followed.
				XmlResolver = null,
				DtdProcessing = DtdProcessing.Ignore,
			};
			xmlReader.MoveToContent();
			// To the end, not only to the first tag. ILSpy stops at the content because it is
			// deciding how to show a blob it already knows is a resource; here the question is
			// asked of any file whose extension said nothing, and "<T>(T value) => value" opens
			// with something a parser will happily call an element. Whether the whole document
			// closes is what tells markup from code that merely starts with a bracket.
			while (xmlReader.Read())
			{
			}
			return true;
		}
		catch (XmlException)
		{
			return false;
		}
	}

	static bool LooksLikeJson(string text)
	{
		// An object or an array; a bare number or string is valid JSON and is also every other
		// text file's first line, so those are left to be text.
		if (FirstContentChar(text) is not ('{' or '['))
			return false;
		try
		{
			using var document = JsonDocument.Parse(text, new JsonDocumentOptions {
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			});
			return true;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	static char FirstContentChar(string text)
	{
		foreach (char c in text)
		{
			if (!char.IsWhiteSpace(c))
				return c;
		}
		return '\0';
	}
}
