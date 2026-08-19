using NUnit.Framework;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>What a log line says about a file, which is what makes it navigable rather than
/// something to go and find by hand.</summary>
public class LogFileRefsTests
{
	[Test]
	public void FindsMsBuildDiagnostic()
	{
		var found = LogFileRefs.Find("/src/App/Program.cs(12,5): error CS1002: ; expected");
		Assert.That(found, Has.Count.EqualTo(1));
		Assert.That(found[0].Path, Is.EqualTo("/src/App/Program.cs"));
		Assert.That(found[0].Line, Is.EqualTo(12));
		Assert.That(found[0].Start, Is.EqualTo(0));
		Assert.That(found[0].Length, Is.EqualTo("/src/App/Program.cs(12,5)".Length));
	}

	[Test]
	public void FindsColonForm()
	{
		var found = LogFileRefs.Find("[git] blame ICSharpCode.Decompiler/CSharp/CallBuilder.cs:1589 -> exit 0");
		Assert.That(found, Has.Count.EqualTo(1));
		Assert.That(found[0].Path, Is.EqualTo("ICSharpCode.Decompiler/CSharp/CallBuilder.cs"));
		Assert.That(found[0].Line, Is.EqualTo(1589));
	}

	[Test]
	public void FindsStackTraceForm()
	{
		var found = LogFileRefs.Find("   at Foo.Bar() in /home/x/Tests/FooTests.cs:line 42");
		Assert.That(found, Has.Count.EqualTo(1));
		Assert.That(found[0].Path, Is.EqualTo("/home/x/Tests/FooTests.cs"));
		Assert.That(found[0].Line, Is.EqualTo(42));
	}

	[Test]
	public void FindsEveryReferenceInOneLine()
	{
		var found = LogFileRefs.Find("copied A.cs:1 to sub/B.cs(7)");
		Assert.That(found.Select(r => r.Path), Is.EqualTo(new[] { "A.cs", "sub/B.cs" }));
		Assert.That(found.Select(r => r.Line), Is.EqualTo(new[] { 1, 7 }));
	}

	[Test]
	public void IgnoresAUrlAndItsPort()
	{
		Assert.That(LogFileRefs.Find("open https://github.com/o/r/blob/main/a.cs:12"), Is.Empty);
		Assert.That(LogFileRefs.Find("listening on http://localhost.dev:5001"), Is.Empty);
	}

	[Test]
	public void IgnoresWhatIsNotAFile()
	{
		Assert.That(LogFileRefs.Find("[git] show 597d505:0 -> exit 0"), Is.Empty);
		Assert.That(LogFileRefs.Find("weighted estimate 1.5:30 minutes"), Is.Empty);
		Assert.That(LogFileRefs.Find("[gh] pr checks 4004 -> exit 0 (1315 ms)"), Is.Empty);
	}

	[Test]
	public void IgnoresALineNumberOfZero()
	{
		Assert.That(LogFileRefs.Find("Foo.cs:0"), Is.Empty);
	}
}
