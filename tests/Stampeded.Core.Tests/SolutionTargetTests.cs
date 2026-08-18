using NUnit.Framework;

using Stampeded.Core.Testing;

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
	public void AnswersNothingWhenThereIsNoSolutionToName()
	{
		Write("Product.csproj", 300);
		Assert.That(SolutionTarget.ForRoot(root), Is.Null);
		Assert.That(SolutionTarget.ForRoot(Path.Combine(root, "missing")), Is.Null);
	}
}
