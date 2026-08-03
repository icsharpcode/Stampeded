using NUnit.Framework;

using Stampeded.Core.Testing;

namespace Stampeded.Core.Tests;

[TestFixture]
public class CoberturaParserTests
{
	const string Cobertura = """
		<?xml version="1.0" encoding="utf-8"?>
		<coverage line-rate="0.5" version="1.9">
		  <sources>
		    <source>/home/user/worktree</source>
		  </sources>
		  <packages>
		    <package name="MyLib">
		      <classes>
		        <class name="MyLib.A" filename="src/A.cs">
		          <lines>
		            <line number="10" hits="3" />
		            <line number="11" hits="0" />
		          </lines>
		        </class>
		        <class name="MyLib.A.Nested" filename="src/A.cs">
		          <lines>
		            <line number="20" hits="1" />
		            <line number="11" hits="2" />
		          </lines>
		        </class>
		        <class name="MyLib.B" filename="/abs/other/B.cs">
		          <lines>
		            <line number="5" hits="0" />
		          </lines>
		        </class>
		      </classes>
		    </package>
		  </packages>
		</coverage>
		""";

	[Test]
	public void ParsesHitsPerFileAndLine()
	{
		var coverage = CoberturaParser.Parse(Cobertura, "/home/user/worktree");

		Assert.That(coverage.Keys, Does.Contain("src/A.cs"));
		var a = coverage["src/A.cs"];
		Assert.That(a[10], Is.EqualTo(3));
		Assert.That(a[20], Is.EqualTo(1));
	}

	[Test]
	public void MergesDuplicateLinesByMaxHits()
	{
		var coverage = CoberturaParser.Parse(Cobertura, "/home/user/worktree");

		// Line 11 appears in two class entries (0 and 2 hits); covered wins.
		Assert.That(coverage["src/A.cs"][11], Is.EqualTo(2));
	}

	[Test]
	public void FilesOutsideTheRootAreSkipped()
	{
		var coverage = CoberturaParser.Parse(Cobertura, "/home/user/worktree");

		Assert.That(coverage.Keys, Has.None.Contains("B.cs"));
	}

	[Test]
	public void EmptyDocumentYieldsNoFiles()
	{
		Assert.That(CoberturaParser.Parse("""<?xml version="1.0"?><coverage />""", "/root"), Is.Empty);
	}
}
