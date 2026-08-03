using NUnit.Framework;

using Stampeded.Core.GitHub;

namespace Stampeded.Core.Tests;

public class GitHubUrlTests
{
	[TestCase("https://github.com/icsharpcode/ILSpy/pull/3933", "icsharpcode", "ILSpy", 3933)]
	[TestCase("https://github.com/icsharpcode/ILSpy/pull/3933/files", "icsharpcode", "ILSpy", 3933)]
	[TestCase("https://github.com/icsharpcode/ILSpy", "icsharpcode", "ILSpy", null)]
	[TestCase("https://github.com/icsharpcode/ILSpy.git", "icsharpcode", "ILSpy", null)]
	[TestCase("git@github.com:icsharpcode/ILSpy.git", "icsharpcode", "ILSpy", null)]
	[TestCase("github.com/icsharpcode/ILSpy/", "icsharpcode", "ILSpy", null)]
	[TestCase("icsharpcode/ILSpy", "icsharpcode", "ILSpy", null)]
	public void ParsesRepoAndPrForms(string input, string owner, string repo, int? pr)
	{
		Assert.That(GitHubUrl.TryParse(input, out string o, out string r, out int? n), Is.True);
		Assert.That((o, r, n), Is.EqualTo((owner, repo, pr)));
	}

	[TestCase("")]
	[TestCase("not a url")]
	[TestCase("https://gitlab.com/foo/bar")]
	public void RejectsNonGitHubInput(string input)
	{
		Assert.That(GitHubUrl.TryParse(input, out _, out _, out _), Is.False);
	}

	[Test]
	public void RemoteMatchingIsCaseInsensitiveAndIgnoresGitSuffix()
	{
		Assert.That(GitHubUrl.RemoteMatches("git@github.com:ICSharpCode/ilspy.git", "icsharpcode", "ILSpy"), Is.True);
		Assert.That(GitHubUrl.RemoteMatches("https://github.com/other/ILSpy.git", "icsharpcode", "ILSpy"), Is.False);
	}
}
