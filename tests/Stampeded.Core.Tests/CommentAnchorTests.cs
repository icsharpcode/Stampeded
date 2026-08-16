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

	// GitHub's excerpt of what a comment was written against: it ends at the commented line.
	const string DiffHunk = "@@ -8,5 +8,6 @@ class C\n \tint x;\n-\tvoid Old()\n+\tvoid New()\n+\t{";

	[Test]
	public void AnchorsAPostedCommentAtTheLastLineOfItsOwnSide()
	{
		var anchor = CommentAnchor.FromDiffHunk("f.cs", oldSide: false, originalLine: 12, DiffHunk);

		Assert.That(anchor, Is.Not.Null);
		Assert.That(anchor!.LineText, Is.EqualTo("\t{"));
		Assert.That(anchor.ContextBefore, Is.EqualTo(new[] { "\tint x;", "\tvoid New()" }),
			"the added lines and the context above them, not the removed ones");
		Assert.That(anchor.Line, Is.EqualTo(12));
	}

	[Test]
	public void TheOldSideOfTheSameHunkAnchorsOnItsRemovedLine()
	{
		var anchor = CommentAnchor.FromDiffHunk("f.cs", oldSide: true, originalLine: 9, DiffHunk);

		Assert.That(anchor, Is.Not.Null);
		Assert.That(anchor!.LineText, Is.EqualTo("\tvoid Old()"));
		Assert.That(anchor.ContextBefore, Is.EqualTo(new[] { "\tint x;" }));
	}

	[Test]
	public void AHunkWithNothingOfThatSideAnchorsNowhere()
	{
		// Only additions: the old side of it is empty, so there is no line to anchor to.
		Assert.That(CommentAnchor.FromDiffHunk("f.cs", oldSide: true, 3, "@@ -0,0 +1,2 @@\n+one\n+two"),
			Is.Null);
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
