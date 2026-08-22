using NUnit.Framework;

using Stampeded.Core.Roslyn;

namespace Stampeded.Core.Tests;

/// <summary>
/// The mapping between the repo-relative paths git speaks and the absolute ones Roslyn
/// reports. Everything positional goes through it, so a mismatch takes out the whole
/// semantic layer at once rather than one feature.
/// </summary>
public class WorkspacePathTests
{
	[Test]
	public async Task RoundTripsAPathWithTheSeparatorsGitUses()
	{
		string dir = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(Path.Combine(dir, "src", "Lib"));
		try
		{
			File.WriteAllText(Path.Combine(dir, "src", "Lib", "C.cs"), "class C { }");
			var service = new RoslynWorkspaceService();
			await service.LoadAsync(dir, null, CancellationToken.None);

			// Forward slashes, as they arrive from git diff on every platform.
			string absolute = service.ToAbsolutePath("src/Lib/C.cs");

			Assert.That(absolute, Is.EqualTo(Path.Combine(dir, "src", "Lib", "C.cs")),
				"the platform's separators, or the document index cannot be keyed by it");
			Assert.That(service.ToRelativePath(absolute), Is.EqualTo("src/Lib/C.cs"));
		}
		finally
		{
			TempDirectory.Delete(dir);
		}
	}

	[Test]
	public async Task RejectsAPathOutsideTheWorktree()
	{
		string root = Path.Combine(Path.GetTempPath(), "stampeded-test-" + Guid.NewGuid().ToString("N")[..8]);
		Directory.CreateDirectory(root);
		string sibling = root + "-other";
		Directory.CreateDirectory(sibling);
		try
		{
			var service = new RoslynWorkspaceService();
			await service.LoadAsync(root, null, CancellationToken.None);

			Assert.That(service.ToRelativePath(Path.Combine(sibling, "C.cs")), Is.Null,
				"a sibling whose name starts with the worktree's is not inside it");
			Assert.That(service.ToRelativePath(root), Is.Null, "the worktree itself is not a file in it");
		}
		finally
		{
			TempDirectory.Delete(root);
			TempDirectory.Delete(sibling);
		}
	}
}
