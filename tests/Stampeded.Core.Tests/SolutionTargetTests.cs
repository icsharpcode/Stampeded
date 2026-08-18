using NUnit.Framework;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>Which solution a dotnet command names, which is what stands between a repository
/// with several of them and "MSB1011: Specify which project or solution file to use".</summary>
public class SolutionTargetTests
{
	string root = "";

	[SetUp]
	public void SetUp()
	{
		root = Path.Combine(Path.GetTempPath(), "stampeded-solution-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(root))
			Directory.Delete(root, recursive: true);
	}

	void Write(string name, int size) => File.WriteAllText(Path.Combine(root, name), new string('x', size));

	[Test]
	public void PicksTheLargestSolutionWhenSeveralAreThere()
	{
		Write("Product.sln", 4000);
		Write("Product.Installer.sln", 200);
		Assert.That(SolutionTarget.ForRoot(root), Is.EqualTo("Product.sln"));
	}

	[Test]
	public void PrefersACrossPlatformFilterOffWindows()
	{
		Write("Product.sln", 4000);
		Write("Product.XPlat.slnf", 100);
		Assert.That(SolutionTarget.ForRoot(root),
			Is.EqualTo(OperatingSystem.IsWindows() ? "Product.sln" : "Product.XPlat.slnf"));
	}

	[Test]
	public void FallsBackToTheNewSolutionFormat()
	{
		Write("Product.slnx", 300);
		Assert.That(SolutionTarget.ForRoot(root), Is.EqualTo("Product.slnx"));
	}

	[Test]
	public void SemanticsFollowTheSolutionAFilterFiltersRatherThanTheFilter()
	{
		Write("Product.sln", 4000);
		File.WriteAllText(Path.Combine(root, "Product.XPlat.slnf"),
			"{ \"solution\": { \"path\": \"Product.sln\", \"projects\": [ \"src\\\\A\\\\A.csproj\" ] } }");
		// Building goes through the filter, which is the point of having one; Roslyn cannot open
		// a filter, so the compilation is of what the filter filters.
		Assert.That(SolutionTarget.ForRoot(root, "Product.XPlat.slnf"), Is.EqualTo("Product.XPlat.slnf"));
		Assert.That(SolutionTarget.ForSemantics(root, "Product.XPlat.slnf"), Is.EqualTo("Product.sln"));
	}

	[Test]
	public void SemanticsFallBackWhenAFilterNamesNothingReadable()
	{
		Write("Product.sln", 4000);
		File.WriteAllText(Path.Combine(root, "Broken.slnf"), "not json at all");
		Assert.That(SolutionTarget.ForSemantics(root, "Broken.slnf"), Is.EqualTo("Product.sln"));
	}

	[Test]
	public void AnswersNothingWhenThereIsNoSolutionToName()
	{
		Write("Product.csproj", 300);
		Assert.That(SolutionTarget.ForRoot(root), Is.Null);
		Assert.That(SolutionTarget.ForRoot(Path.Combine(root, "missing")), Is.Null);
	}
}
