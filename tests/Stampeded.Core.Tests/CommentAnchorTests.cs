using NUnit.Framework;

using Stampeded.Core.Review;

namespace Stampeded.Core.Tests;

[TestFixture]
public class CommentAnchorTests
{
	static readonly string[] File1 = [
		"using System;",         // 1
		"",                      // 2
		"class C",               // 3
		"{",                     // 4
		"\tint x;",              // 5
		"\tvoid M()",            // 6
		"\t{",                   // 7
		"\t\tx = 1;",            // 8
		"\t}",                   // 9
		"}",                     // 10
	];

	[Test]
	public void CreateCapturesLineAndContext()
	{
		var anchor = CommentAnchor.Create("f.cs", oldSide: false, line: 6, File1);

		Assert.That(anchor.LineText, Is.EqualTo("\tvoid M()"));
		Assert.That(anchor.ContextBefore, Is.EqualTo(new[] { "{", "\tint x;" }));
		Assert.That(anchor.ContextAfter, Is.EqualTo(new[] { "\t{", "\t\tx = 1;" }));
	}

	[Test]
	public void ReattachToIdenticalFileReturnsSameLine()
	{
		var anchor = CommentAnchor.Create("f.cs", false, 6, File1);
		Assert.That(anchor.Reattach(File1), Is.EqualTo(6));
	}

	[Test]
	public void ReattachSurvivesInsertionAbove()
	{
		var anchor = CommentAnchor.Create("f.cs", false, 6, File1);
		string[] shifted = ["// new header", "// more header", .. File1];
		Assert.That(anchor.Reattach(shifted), Is.EqualTo(8));
	}

	[Test]
	public void ReattachDisambiguatesDuplicateLinesByContext()
	{
		string[] file = [
			"if (a)",     // 1
			"{",          // 2
			"\treturn;",  // 3
			"}",          // 4
			"if (b)",     // 5
			"{",          // 6
			"\treturn;",  // 7
			"}",          // 8
		];
		var anchor = CommentAnchor.Create("f.cs", false, 7, file);
		string[] shifted = ["// x", .. file];
		Assert.That(anchor.Reattach(shifted), Is.EqualTo(8));
	}

	[Test]
	public void ReattachFuzzyMatchesNearOriginalLineWhenContextChanged()
	{
		var anchor = CommentAnchor.Create("f.cs", false, 6, File1);
		// Same line text still present, but its surroundings were rewritten.
		string[] rewritten = [
			"using System;",
			"class C",
			"{",
			"\tvoid M()",     // 4: context differs, text matches, within fuzz range
			"\t{",
			"\t}",
			"}",
		];
		Assert.That(anchor.Reattach(rewritten), Is.EqualTo(4));
	}

	[Test]
	public void ReattachReturnsNullWhenLineIsGone()
	{
		var anchor = CommentAnchor.Create("f.cs", false, 6, File1);
		string[] without = [.. File1.Where(l => l != "\tvoid M()")];
		Assert.That(anchor.Reattach(without), Is.Null);
	}

	[Test]
	public void FuzzyMatchOutsideRangeIsRejected()
	{
		var anchor = CommentAnchor.Create("f.cs", false, 6, File1);
		// Line text reappears far away (> fuzz range) with different context.
		var far = new List<string>();
		for (int i = 0; i < 60; i++)
			far.Add($"// filler {i}");
		far.Add("\tvoid M()");
		Assert.That(anchor.Reattach(far), Is.Null);
	}
}

public class CommentAnchorApproximationTests
{
	[Test]
	public void ApproximatesFromSurvivingContextAfterTheLineIsGone()
	{
		string[] original = ["header", "alpha", "target line", "omega", "footer"];
		var anchor = Stampeded.Core.Review.CommentAnchor.Create("f.cs", false, 3, original);
		// The commented line vanished; its context moved down by two inserted lines.
		string[] current = ["new1", "new2", "header", "alpha", "omega", "footer"];
		Assert.That(anchor.Reattach(current), Is.Null, "precondition: exact reattach fails");
		int approx = anchor.Approximate(current);
		// Ghost position between alpha (line 4) and omega (line 5) -> attaches at line 4.
		Assert.That(approx, Is.EqualTo(4));
	}

	[Test]
	public void FallsBackToClampedOriginalLineWithoutContextMatches()
	{
		var anchor = new Stampeded.Core.Review.CommentAnchor("f.cs", false, 40, "gone",
			["also gone"], ["gone too"]);
		Assert.That(anchor.Approximate(["a", "b", "c"]), Is.EqualTo(3));
	}
}
