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

	/// <summary>
	/// The real file list of ILSpy's null-coalescing-assignment branch. Twelve decompiler files
	/// and a runner method, of which exactly one thing names a test: the test case. Nothing in
	/// the suite refers to what that file declares, so this is the only way to reach the answer
	/// without inferring it.
	/// </summary>
	[Test]
	public void AffectedFixtures_NamesTheTestOfAChangedTestCase()
	{
		var fixtures = FixtureAssemblies.AffectedFixtures([
			"ICSharpCode.Decompiler.Tests/PrettyTestRunner.cs",
			"ICSharpCode.Decompiler.Tests/TestCases/Pretty/NullCoalescingAssign.cs",
			"ICSharpCode.Decompiler/CSharp/CSharpDecompiler.cs",
			"ICSharpCode.Decompiler/CSharp/ExpressionBuilder.cs",
			"ICSharpCode.Decompiler/CSharp/Syntax/Expressions/AssignmentExpression.cs",
			"ICSharpCode.Decompiler/FlowAnalysis/DataFlowVisitor.cs",
			"ICSharpCode.Decompiler/IL/ILVariable.cs",
			"ICSharpCode.Decompiler/IL/Instructions.cs",
			"ICSharpCode.Decompiler/IL/Instructions.tt",
			"ICSharpCode.Decompiler/IL/Instructions/CompoundAssignmentInstruction.cs",
			"ICSharpCode.Decompiler/IL/Transforms/ExpressionTransforms.cs",
			"ICSharpCode.Decompiler/IL/Transforms/ILInlining.cs",
			"ICSharpCode.Decompiler/IL/Transforms/NullCoalescingAssignTransform.cs",
			"ICSharpCode.Decompiler/IL/Transforms/TransformAssignment.cs",
		]);

		Assert.That(fixtures.Select(f => f.Name), Is.EqualTo(new[] { "NullCoalescingAssign" }));
	}

	[Test]
	public void IsFixtureSource_TellsATestCaseFromTheSuiteAroundIt()
	{
		Assert.Multiple(() => {
			Assert.That(FixtureAssemblies.IsFixtureSource(
				"ICSharpCode.Decompiler.Tests/TestCases/Pretty/NullCoalescingAssign.cs"), Is.True);
			Assert.That(FixtureAssemblies.IsFixtureSource(
				"ICSharpCode.Decompiler.Tests/TestCases/ILPretty/Issue1234.il"), Is.True);
			// A test, not a test case: what it declares is referred to, so it is traced.
			Assert.That(FixtureAssemblies.IsFixtureSource(
				"ICSharpCode.Decompiler.Tests/PrettyTestRunner.cs"), Is.False);
			// Expected output of a fixture, not a source the suite compiles.
			Assert.That(FixtureAssemblies.IsFixtureSource(
				"ICSharpCode.Decompiler.Tests/TestCases/Pretty/NullCoalescingAssign.expected.txt"), Is.False);
		});
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
