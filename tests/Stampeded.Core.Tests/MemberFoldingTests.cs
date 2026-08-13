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
		Assert.That(regions, Does.Contain(new MemberFoldRegion(1, 17)));
		Assert.That(regions, Does.Contain(new MemberFoldRegion(5, 7)));   // Multi
		Assert.That(regions, Does.Contain(new MemberFoldRegion(9, 11)));  // M
		Assert.That(regions, Does.Contain(new MemberFoldRegion(13, 16))); // event E
		Assert.That(regions.Any(r => r.StartLine == 3 && r.EndLine == 3), Is.False);
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
		Assert.That(regions, Does.Contain(new MemberFoldRegion(5, 7)));
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

		Assert.That(regions, Does.Contain(new MemberFoldRegion(2, 7)));
		Assert.That(regions.Any(r => r.StartLine == 1), Is.False);
	}

	[Test]
	public void BrokenCodeStillYieldsRegions()
	{
		var regions = MemberFolding.Compute("class C {\n void M() {\n int x\n }\n}\n");
		Assert.That(regions.Count, Is.GreaterThanOrEqualTo(1));
	}
}
