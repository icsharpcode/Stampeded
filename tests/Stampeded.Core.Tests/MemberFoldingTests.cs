using NUnit.Framework;

using Stampeded.Core.Roslyn;

namespace Stampeded.Core.Tests;

public class MemberFoldingTests
{
	[Test]
	public void FoldsTypesMethodsPropertiesAndEvents()
	{
		string source = """
			class C
			{
				public int SingleLine { get; set; }

				public int Multi {
					get { return 1; }
				}

				void M()
				{
				}

				public event System.EventHandler E {
					add { }
					remove { }
				}
			}
			""";
		var regions = MemberFolding.Compute(source);

		// The class spans everything; single-line members produce no region.
		Assert.That(regions, Does.Contain(new MemberFoldRegion(1, 17, 2)));
		Assert.That(regions, Does.Contain(new MemberFoldRegion(5, 7, 5)));   // Multi
		Assert.That(regions, Does.Contain(new MemberFoldRegion(9, 11, 10)));  // M
		Assert.That(regions, Does.Contain(new MemberFoldRegion(13, 16, 13))); // event E
		Assert.That(regions.Any(r => r.StartLine == 3 && r.EndLine == 3), Is.False);
	}

	[Test]
	public void FoldsARegionToItsEndRegion()
	{
		string source = """
			class C
			{
				#region Commands
				void A()
				{
				}

				void B()
				{
				}
				#endregion
			}
			""";
		var regions = MemberFolding.Compute(source);

		Assert.That(regions, Does.Contain(new MemberFoldRegion(3, 11, 3)));
		Assert.That(regions, Does.Contain(new MemberFoldRegion(4, 6, 5)));  // A, still foldable inside
	}

	[Test]
	public void LeavesOutARegionWithNoEndAndOneThatCrossesAMember()
	{
		string unclosed = """
			class C
			{
				#region Never closed
				void A()
				{
				}
			}
			""";
		Assert.That(MemberFolding.Compute(unclosed).Any(r => r.StartLine == 3), Is.False);

		// Opens inside A and closes after it: a fold that no folding manager can nest.
		string crossing = """
			class C
			{
				void A()
				{
					#region Inside
				}

				void B()
				{
				}
				#endregion
			}
			""";
		Assert.That(MemberFolding.Compute(crossing).Any(r => r.StartLine == 5), Is.False);
		Assert.That(MemberFolding.Compute(crossing), Does.Contain(new MemberFoldRegion(3, 6, 4)));
	}

	[Test]
	public void StartsAMemberAtItsDeclarationRatherThanItsAttributes()
	{
		string source = """
			class C
			{
				[Test]
				[Category("slow")]
				public void M()
				{
				}

				[Obsolete]
				public int P => 1;
			}
			""";
		var regions = MemberFolding.Compute(source);

		// Collapsing M has to leave its attributes on screen: they are what tells the reader
		// what the member is, which is the one thing a collapsed member cannot say for itself.
		Assert.That(regions, Does.Contain(new MemberFoldRegion(5, 7, 6)));
		Assert.That(regions.Any(r => r.StartLine is 3 or 4), Is.False);
		// P occupies a single line once its attribute is not counted, so it does not fold.
		Assert.That(regions.Any(r => r.StartLine is 9 or 10), Is.False);
	}

	[Test]
	public void FoldsATypeFromItsDeclarationNotItsAttributes()
	{
		string source = """
			[Serializable]
			class C
			{
				void M()
				{
				}
			}
			""";
		var regions = MemberFolding.Compute(source);

		Assert.That(regions, Does.Contain(new MemberFoldRegion(2, 7, 3)));
		Assert.That(regions.Any(r => r.StartLine == 1), Is.False);
	}

	[Test]
	public void BrokenCodeStillYieldsRegions()
	{
		var regions = MemberFolding.Compute("class C {\n void M() {\n int x\n }\n}\n");
		Assert.That(regions.Count, Is.GreaterThanOrEqualTo(1));
	}

	[Test]
	public void EndsAHeaderAtTheTokenThatOpensTheBody()
	{
		// A declaration is often written over several lines, and it is the whole of it that
		// says what the code under it belongs to.
		string source = """
			class Wrapped
				: System.IDisposable
			{
				public void Dispose()
				{
				}

				static T Pick<T>(
					T first,
					T second)
					where T : class
				{
					return first;
				}

				public int Answer =>
					42;

				public string Name
				{
					get;
					set;
				}
			}
			""";
		var regions = MemberFolding.Compute(source);

		// The type's header runs to the brace under its base list.
		Assert.That(HeaderEnd(regions, 1), Is.EqualTo(3));
		// A wrapped parameter list and a constraint are part of the signature.
		Assert.That(HeaderEnd(regions, 8), Is.EqualTo(12));
		// An expression body opens at its arrow, wherever that sits.
		Assert.That(HeaderEnd(regions, 16), Is.EqualTo(16));
		// An accessor list opening on the next line ends the header there.
		Assert.That(HeaderEnd(regions, 19), Is.EqualTo(20));
	}

	static int HeaderEnd(IReadOnlyList<MemberFoldRegion> regions, int startLine)
		=> regions.Single(r => r.StartLine == startLine).HeaderEndLine;
}
