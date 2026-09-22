using NUnit.Framework;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// The path mapping every semantic provider shares. Each provider used to carry its own copy,
/// and they disagreed: the one without the separator check answered a sibling directory whose
/// name merely starts with the root's, which is a path nothing in the review has.
/// </summary>
public class WorkspacePathHelperTests
{
	static string Root => OperatingSystem.IsWindows() ? @"C:\src\repo" : "/src/repo";

	static string Under(params string[] parts) => Path.Combine([Root, .. parts]);

	[Test]
	public void MapsBothWaysWithTheSeparatorsGitUses()
	{
		string absolute = WorkspacePaths.ToAbsolute(Root, "src/Lib/C.cs");

		Assert.That(absolute, Is.EqualTo(Under("src", "Lib", "C.cs")),
			"the platform's separators, or a document index cannot be keyed by it");
		Assert.That(WorkspacePaths.ToRelative(Root, absolute), Is.EqualTo("src/Lib/C.cs"),
			"git's separators, on the way back");
	}

	[Test]
	public void RejectsASiblingWhoseNameStartsWithTheRoot()
	{
		string sibling = Path.Combine(Root + "-other", "C.cs");

		Assert.That(WorkspacePaths.ToRelative(Root, sibling), Is.Null,
			"a path outside the tree has no repository-relative form, and answering '-other/C.cs' "
				+ "names a file the review does not have");
	}

	[Test]
	public void RejectsTheRootItselfAndWhatIsAboveIt()
	{
		Assert.That(WorkspacePaths.ToRelative(Root, Root), Is.Null, "the root is not a file in itself");
		Assert.That(WorkspacePaths.ToRelative(Root, Path.GetDirectoryName(Root)!), Is.Null);
	}

	[Test]
	public void AcceptsARootSpelledWithATrailingSeparator()
	{
		// Where a root comes from decides how it is spelled, and a worktree path assembled by
		// hand can carry one.
		string absolute = Under("C.cs");

		Assert.That(WorkspacePaths.ToRelative(Root + Path.DirectorySeparatorChar, absolute),
			Is.EqualTo("C.cs"));
	}

	[Test]
	[Platform("Win", Reason = "only Windows compares paths without regard to case")]
	public void IgnoresCaseWhereTheFilesystemDoes()
	{
		Assert.That(WorkspacePaths.ToRelative(Root, Under("SRC", "C.cs")), Is.EqualTo("SRC/C.cs"));
	}
}
