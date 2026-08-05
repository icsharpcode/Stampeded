using NUnit.Framework;

using Stampeded.Core.Review;

namespace Stampeded.Core.Tests;

public class FixtureAssembliesTests
{
	[Test]
	public void AffectedFixtures_FiltersToDecompilerTestCases()
	{
		var fixtures = FixtureAssemblies.AffectedFixtures([
			"ICSharpCode.Decompiler.Tests/TestCases/Pretty/DeconstructionTests.cs",
			"ICSharpCode.Decompiler.Tests/TestCases/ILPretty/Issue1234.il",
			"ICSharpCode.Decompiler.Tests/TestCases/ILPretty/Issue1234.cs",
			"ICSharpCode.Decompiler.Tests/TestCases/Pretty/DeconstructionTests.opt.roslyn.il",
			"ICSharpCode.Decompiler.Tests/PrettyTestRunner.cs",
			"ICSharpCode.Decompiler/CSharp/CSharpDecompiler.cs",
			"ILSpy/MainWindow.axaml.cs",
		]);
		Assert.That(fixtures, Is.EqualTo(new[] {
			("ICSharpCode.Decompiler.Tests/TestCases/Pretty", "DeconstructionTests"),
			("ICSharpCode.Decompiler.Tests/TestCases/ILPretty", "Issue1234"),
		}));
	}

	[Test]
	public void IsAssemblyOf_MatchesVariantSuffixesButNotPrefixNames()
	{
		Assert.Multiple(() => {
			Assert.That(FixtureAssemblies.IsAssemblyOf("AnonymousTypes", "AnonymousTypes.dll"), Is.True);
			Assert.That(FixtureAssemblies.IsAssemblyOf("AnonymousTypes", "AnonymousTypes.opt.roslyn4.dll"), Is.True);
			Assert.That(FixtureAssemblies.IsAssemblyOf("AnonymousTypes", "AnonymousTypes.exe"), Is.True);
			Assert.That(FixtureAssemblies.IsAssemblyOf("AnonymousTypes", "AnonymousTypes2.dll"), Is.False);
			Assert.That(FixtureAssemblies.IsAssemblyOf("Anonymous", "AnonymousTypes.dll"), Is.False);
			Assert.That(FixtureAssemblies.IsAssemblyOf("AnonymousTypes", "AnonymousTypes.cs"), Is.False);
			Assert.That(FixtureAssemblies.IsAssemblyOf("AnonymousTypes", "AnonymousTypes.opt.roslyn.pdb"), Is.False);
		});
	}
}
