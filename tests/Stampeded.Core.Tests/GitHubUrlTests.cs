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
	// What a browser hands over: the tab that was open, the comment that was linked, the
	// whitespace toggle. None of it names a different repository.
	[TestCase("https://github.com/icsharpcode/ILSpy/pull/3933/files#diff-abc123", "icsharpcode", "ILSpy", 3933)]
	[TestCase("https://github.com/icsharpcode/ILSpy/pull/3933#issuecomment-4001", "icsharpcode", "ILSpy", 3933)]
	[TestCase("https://github.com/icsharpcode/ILSpy/pull/3933?w=1", "icsharpcode", "ILSpy", 3933)]
	[TestCase("https://github.com/icsharpcode/ILSpy/pull/3933/commits/abcdef1", "icsharpcode", "ILSpy", 3933)]
	[TestCase("https://github.com/icsharpcode/ILSpy/tree/master", "icsharpcode", "ILSpy", null)]
	[TestCase("https://github.com/icsharpcode/ILSpy/issues/829", "icsharpcode", "ILSpy", null)]
	[TestCase("https://github.com/icsharpcode/ILSpy/blob/master/README.md", "icsharpcode", "ILSpy", null)]
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
	public void MatchesAnyRemoteOfACheckout()
	{
		// What git config prints for a checkout that tracks a repository and a fork of it.
		string config = "remote.origin.url git@github.com:icsharpcode/ILSpy.git\n"
			+ "remote.sailro.url git@github.com:sailro/ILSpy.git";

		Assert.That(GitHubUrl.AnyRemoteMatches(config, "icsharpcode", "ILSpy"), Is.True);
		Assert.That(GitHubUrl.AnyRemoteMatches(config, "sailro", "ILSpy"), Is.True,
			"a fork names the same checkout");
		Assert.That(GitHubUrl.AnyRemoteMatches(config, "other", "ILSpy"), Is.False);
		Assert.That(GitHubUrl.AnyRemoteMatches("", "icsharpcode", "ILSpy"), Is.False);
	}

	[Test]
	public void RemoteMatchingIsCaseInsensitiveAndIgnoresGitSuffix()
	{
		Assert.That(GitHubUrl.RemoteMatches("git@github.com:ICSharpCode/ilspy.git", "icsharpcode", "ILSpy"), Is.True);
		Assert.That(GitHubUrl.RemoteMatches("https://github.com/other/ILSpy.git", "icsharpcode", "ILSpy"), Is.False);
	}
}
