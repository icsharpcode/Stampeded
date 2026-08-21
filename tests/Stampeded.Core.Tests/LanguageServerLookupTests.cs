using NUnit.Framework;

using Stampeded.Core.Lsp;

namespace Stampeded.Core.Tests;

/// <summary>
/// Finding a server on PATH, for the platform that does not agree that a file which exists is
/// a file that runs. Written from Linux about Windows, which is the whole reason the platform
/// is a parameter.
/// </summary>
public class LanguageServerLookupTests
{
	const string Pathext = ".COM;.EXE;.BAT;.CMD";

	[Test]
	public void OnWindowsABareNameIsOnlyEverTriedWithAnExtension()
	{
		var names = LanguageServers.ExecutableNames("C:/nodejs/npx", windows: true, Pathext).ToList();

		// npm writes a POSIX shell script under the bare name next to the .cmd; starting it
		// gets "not a valid application for this OS platform" and nothing else.
		Assert.That(names, Does.Not.Contain("C:/nodejs/npx"));
		Assert.That(names, Does.Contain("C:/nodejs/npx.cmd").IgnoreCase);
		Assert.That(names.IndexOf("C:/nodejs/npx.EXE"), Is.LessThan(names.IndexOf("C:/nodejs/npx.CMD")),
			"PATHEXT's order is the order Windows itself would try");
	}

	[Test]
	public void ANameThatAlreadySaysWhatItIsStaysAsItIs()
	{
		Assert.That(LanguageServers.ExecutableNames("C:/Python/python.exe", windows: true, Pathext),
			Is.EqualTo(new[] { "C:/Python/python.exe" }));
	}

	[Test]
	public void EverywhereElseTheNameIsTheName()
	{
		Assert.That(LanguageServers.ExecutableNames("/usr/bin/pylsp", windows: false, null),
			Is.EqualTo(new[] { "/usr/bin/pylsp" }));
	}
}

/// <summary>
/// The paths a server names. Everything built on vscode-uri - pyright included - writes a
/// Windows drive as an escape, and a review that cannot turn that back into a path answers
/// go-to-definition with nothing at all.
/// </summary>
public class LspUriTests
{
	[Test]
	public void ADriveWrittenAsAnEscapeIsStillADrive()
	{
		string? path = LspUri.ToPath("file:///d%3A/Projects/master/app.py");

		Assert.That(path, Is.Not.Null);
		Assert.That(path, Does.StartWith("d:"), "not a rooted path with the drive buried in it");
		Assert.That(path, Does.EndWith("app.py"));
	}

	[Test]
	public void APosixPathIsUntouched()
	{
		Assert.That(LspUri.ToPath("file:///home/reader/app.py"), Is.EqualTo("/home/reader/app.py"));
	}

	[Test]
	public void WhatIsNotAFileIsNotAPath()
	{
		Assert.That(LspUri.ToPath("untitled:Untitled-1"), Is.Null);
		Assert.That(LspUri.ToPath("not a uri"), Is.Null);
	}
}
