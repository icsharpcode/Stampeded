using NUnit.Framework;

using Stampeded.Core.AzureDevOps;

namespace Stampeded.Core.Tests;

public class AzureDevOpsUrlTests
{
	[TestCase("https://dev.azure.com/contoso/Widgets/_git/widgets-api", "contoso", "Widgets", "widgets-api", null)]
	[TestCase("https://dev.azure.com/contoso/Widgets/_git/widgets-api/pullrequest/417", "contoso", "Widgets", "widgets-api", 417)]
	// What the portal's clone button writes: the organization again, in front of the host.
	[TestCase("https://contoso@dev.azure.com/contoso/Widgets/_git/widgets-api", "contoso", "Widgets", "widgets-api", null)]
	[TestCase("https://contoso.visualstudio.com/Widgets/_git/widgets-api", "contoso", "Widgets", "widgets-api", null)]
	[TestCase("https://contoso.visualstudio.com/DefaultCollection/Widgets/_git/widgets-api/pullrequest/8", "contoso", "Widgets", "widgets-api", 8)]
	[TestCase("git@ssh.dev.azure.com:v3/contoso/Widgets/widgets-api", "contoso", "Widgets", "widgets-api", null)]
	[TestCase("https://dev.azure.com/contoso/Widgets/_git/widgets-api.git", "contoso", "Widgets", "widgets-api", null)]
	// A project and a repository may both have spaces in them, and a URL carries them encoded.
	[TestCase("https://dev.azure.com/contoso/My%20Project/_git/My%20Repo", "contoso", "My Project", "My Repo", null)]
	// What a browser hands over beyond the address: the tab that was open, a discussion anchor.
	[TestCase("https://dev.azure.com/contoso/Widgets/_git/widgets-api/pullrequest/417?_a=files", "contoso", "Widgets", "widgets-api", 417)]
	[TestCase("https://dev.azure.com/contoso/Widgets/_git/widgets-api/pullrequest/417#1234", "contoso", "Widgets", "widgets-api", 417)]
	public void Parses(string input, string org, string project, string repo, int? pr)
	{
		Assert.That(AzureDevOpsUrl.TryParse(input, out string o, out string p, out string r, out int? number), Is.True);
		Assert.Multiple(() => {
			Assert.That(o, Is.EqualTo(org));
			Assert.That(p, Is.EqualTo(project));
			Assert.That(r, Is.EqualTo(repo));
			Assert.That(number, Is.EqualTo(pr));
		});
	}

	// GitHub's URLs must not be read as Azure DevOps ones: the host decides which service a
	// review talks to, and a URL read by both would settle it by the order they are tried.
	[TestCase("https://github.com/icsharpcode/ILSpy/pull/3933")]
	[TestCase("git@github.com:icsharpcode/ILSpy.git")]
	[TestCase("icsharpcode/ILSpy")]
	[TestCase("")]
	public void RefusesWhatIsNotAzureDevOps(string input)
		=> Assert.That(AzureDevOpsUrl.TryParse(input, out _, out _, out _, out _), Is.False);

	[Test]
	public void MatchesARemoteOfTheCheckout()
	{
		string config = """
			remote.origin.url https://contoso@dev.azure.com/contoso/Widgets/_git/widgets-api
			remote.upstream.url git@github.com:icsharpcode/ILSpy.git
			""";
		Assert.Multiple(() => {
			Assert.That(AzureDevOpsUrl.AnyRemoteMatches(config, "contoso", "Widgets", "widgets-api"), Is.True);
			// The same organization and project, another repository: not this checkout.
			Assert.That(AzureDevOpsUrl.AnyRemoteMatches(config, "contoso", "Widgets", "widgets-web"), Is.False);
		});
	}
}
