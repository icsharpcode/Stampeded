using NUnit.Framework;

using Stampeded.Core.Bitbucket;

namespace Stampeded.Core.Tests;

public class BitbucketUrlTests
{
	[TestCase("https://bitbucket.example.com/projects/PRJ/repos/widgets", "https://bitbucket.example.com", "PRJ", "widgets", null)]
	[TestCase("https://bitbucket.example.com/projects/PRJ/repos/widgets/pull-requests/17", "https://bitbucket.example.com", "PRJ", "widgets", 17)]
	[TestCase("https://bitbucket.example.com/projects/PRJ/repos/widgets/pull-requests/17/diff", "https://bitbucket.example.com", "PRJ", "widgets", 17)]
	[TestCase("https://bitbucket.example.com/projects/PRJ/repos/widgets/pull-requests/17?commentId=12", "https://bitbucket.example.com", "PRJ", "widgets", 17)]
	[TestCase("https://code.example.com/bitbucket/projects/PRJ/repos/widgets/pull-requests/17", "https://code.example.com/bitbucket", "PRJ", "widgets", 17)]
	[TestCase("https://bitbucket.example.com/scm/PRJ/widgets.git", "https://bitbucket.example.com", "PRJ", "widgets", null)]
	[TestCase("git@bitbucket.example.com:PRJ/widgets.git", "https://bitbucket.example.com", "PRJ", "widgets", null)]
	[TestCase("ssh://git@bitbucket.example.com:7999/PRJ/widgets.git", "https://bitbucket.example.com", "PRJ", "widgets", null)]
	public void Parses(string input, string baseUrl, string project, string repo, int? pr)
	{
		Assert.That(BitbucketUrl.TryParse(input, out string b, out string p, out string r, out int? n), Is.True);
		Assert.Multiple(() => {
			Assert.That(b, Is.EqualTo(baseUrl));
			Assert.That(p, Is.EqualTo(project));
			Assert.That(r, Is.EqualTo(repo));
			Assert.That(n, Is.EqualTo(pr));
		});
	}

	[TestCase("https://github.com/icsharpcode/ILSpy/pull/3933")]
	[TestCase("https://dev.azure.com/contoso/Widgets/_git/widgets-api")]
	[TestCase("icsharpcode/ILSpy")]
	[TestCase("")]
	public void RefusesWhatIsNotBitbucket(string input)
		=> Assert.That(BitbucketUrl.TryParse(input, out _, out _, out _, out _), Is.False);

	[Test]
	public void MatchesARemoteOfTheCheckout()
	{
		string config = """
			remote.origin.url ssh://git@bitbucket.example.com:7999/PRJ/widgets.git
			remote.upstream.url git@github.com:icsharpcode/ILSpy.git
			""";

		Assert.Multiple(() => {
			Assert.That(BitbucketUrl.AnyRemoteMatches(config, "https://bitbucket.example.com", "PRJ", "widgets"), Is.True);
			Assert.That(BitbucketUrl.AnyRemoteMatches(config, "https://bitbucket.example.com", "PRJ", "other"), Is.False);
		});
	}

	[Test]
	public void RemoteMatchingAllowsAContextPathWhenTheSshRemoteOnlyNamesTheHost()
	{
		Assert.That(BitbucketUrl.RemoteMatches(
			"git@bitbucket.example.com:PRJ/widgets.git",
			"https://bitbucket.example.com/bitbucket",
			"PRJ",
			"widgets"), Is.True);
	}

	[Test]
	public void CloneUrlKeepsSshInput()
	{
		Assert.That(BitbucketUrl.CloneUrlFor(
			"ssh://git@bitbucket.example.com:7999/PRJ/widgets.git",
			"https://bitbucket.example.com",
			"PRJ",
			"widgets"), Is.EqualTo("ssh://git@bitbucket.example.com:7999/PRJ/widgets.git"));
	}
}
