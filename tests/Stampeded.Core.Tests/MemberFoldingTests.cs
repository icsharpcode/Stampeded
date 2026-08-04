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
	public void BrokenCodeStillYieldsRegions()
	{
		var regions = MemberFolding.Compute("class C {\n void M() {\n int x\n }\n}\n");
		Assert.That(regions.Count, Is.GreaterThanOrEqualTo(1));
	}
}
