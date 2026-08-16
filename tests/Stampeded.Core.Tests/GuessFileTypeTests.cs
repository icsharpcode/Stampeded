using NUnit.Framework;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// What a file turns out to be when its name does not say. The answer picks the highlighting,
/// so a wrong "yes" paints a source file as markup and a wrong "no" leaves markup grey.
/// </summary>
[TestFixture]
public class GuessFileTypeTests
{
	[Test]
	public void RecognisesXmlWithAndWithoutADeclaration()
	{
		Assert.That(GuessFileType.DetectTextType("<?xml version=\"1.0\"?>\n<root><a/></root>"),
			Is.EqualTo(FileType.Xml));
		// What a .props or .targets holds: markup that no highlighting definition claims.
		Assert.That(GuessFileType.DetectTextType("<Project>\n  <PropertyGroup />\n</Project>\n"),
			Is.EqualTo(FileType.Xml));
		// Leading whitespace and a comment before the root are still XML.
		Assert.That(GuessFileType.DetectTextType("\n  <!-- why -->\n<Project />"), Is.EqualTo(FileType.Xml));
	}

	[Test]
	public void RecognisesJsonObjectsAndArrays()
	{
		Assert.That(GuessFileType.DetectTextType("{ \"sdk\": { \"version\": \"10.0.100\" } }"),
			Is.EqualTo(FileType.Json));
		Assert.That(GuessFileType.DetectTextType("[1, 2, 3]"), Is.EqualTo(FileType.Json));
		// The dialect the tooling actually writes: comments and a trailing comma.
		Assert.That(GuessFileType.DetectTextType("{\n  // a note\n  \"a\": 1,\n}"), Is.EqualTo(FileType.Json));
	}

	[Test]
	public void LeavesSourceAndProseAsText()
	{
		Assert.That(GuessFileType.DetectTextType("class C { }"), Is.EqualTo(FileType.Text));
		Assert.That(GuessFileType.DetectTextType("# Title\n\nSome prose.\n"), Is.EqualTo(FileType.Text));
		Assert.That(GuessFileType.DetectTextType(""), Is.EqualTo(FileType.Text));
	}

	[Test]
	public void RefusesWhatOnlyStartsLikeMarkupOrAnObject()
	{
		// Generics open with '<' and C# initialisers with '{'; neither parses, so neither counts.
		Assert.That(GuessFileType.DetectTextType("<T>(T value) => value"), Is.EqualTo(FileType.Text));
		Assert.That(GuessFileType.DetectTextType("{ this is not json }"), Is.EqualTo(FileType.Text));
		Assert.That(GuessFileType.DetectTextType("<root><unclosed>"), Is.EqualTo(FileType.Text));
	}

	[Test]
	public void ABareValueIsTextEvenThoughItParsesAsJson()
	{
		// "42" and a quoted line are valid JSON and are also every other file's first line;
		// claiming them would repaint half the repository.
		Assert.That(GuessFileType.DetectTextType("42"), Is.EqualTo(FileType.Text));
		Assert.That(GuessFileType.DetectTextType("\"just a string\""), Is.EqualTo(FileType.Text));
	}
}
