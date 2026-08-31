using NUnit.Framework;

using Stampeded.Core.Roslyn;

namespace Stampeded.Core.Tests;

public class BuildArtifactCleanupTests
{
	string root = "";

	[SetUp]
	public void SetUp()
	{
		root = Path.Combine(Path.GetTempPath(), "stampeded-cleanup-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
	}

	[TearDown]
	public void TearDown()
	{
		TempDirectory.Delete(root);
	}

	static void WriteFile(string directory, string name)
	{
		Directory.CreateDirectory(directory);
		File.WriteAllText(Path.Combine(directory, name), "x");
	}

	[Test]
	public void RemovesObjAndBinOfTheWorktree()
	{
		string worktree = Path.Combine(root, "worktree");
		WriteFile(Path.Combine(worktree, "src", "bin"), "a.dll");
		WriteFile(Path.Combine(worktree, "src", "obj"), "a.cache");
		WriteFile(Path.Combine(worktree, "src"), "A.cs");

		RoslynWorkspaceService.DeleteBuildArtifacts(worktree);

		Assert.Multiple(() => {
			Assert.That(Directory.Exists(Path.Combine(worktree, "src", "bin")), Is.False);
			Assert.That(Directory.Exists(Path.Combine(worktree, "src", "obj")), Is.False);
			Assert.That(File.Exists(Path.Combine(worktree, "src", "A.cs")), Is.True, "sources are not artifacts");
		});
	}

	// A review worktree links its submodules to the real clone. Following such a link puts
	// the walk inside the user's checkout, where a "bin" directory can be committed content.
	[Test]
	public void DoesNotFollowASymlinkOutOfTheWorktree()
	{
		string worktree = Path.Combine(root, "worktree");
		string clone = Path.Combine(root, "clone");
		WriteFile(Path.Combine(clone, "fixtures", "bin"), "committed.dll");
		Directory.CreateDirectory(worktree);
		Directory.CreateSymbolicLink(Path.Combine(worktree, "submodule"), clone);
		WriteFile(Path.Combine(worktree, "src", "bin"), "a.dll");

		RoslynWorkspaceService.DeleteBuildArtifacts(worktree);

		Assert.Multiple(() => {
			Assert.That(File.Exists(Path.Combine(clone, "fixtures", "bin", "committed.dll")), Is.True,
				"the real clone's committed files must survive");
			Assert.That(Directory.Exists(Path.Combine(worktree, "src", "bin")), Is.False);
			Assert.That(Directory.Exists(Path.Combine(worktree, "submodule")), Is.True, "the link itself stays");
		});
	}
}
