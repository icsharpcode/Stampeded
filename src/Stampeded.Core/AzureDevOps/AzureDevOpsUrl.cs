using System.Text.RegularExpressions;

namespace Stampeded.Core.AzureDevOps;

/// <summary>
/// Parses Azure DevOps repository and pull-request URLs into organization, project, repository
/// and an optional pull-request id. Four shapes reach a reader: what the portal's clone button
/// writes, the old visualstudio.com host it still answers on, its DefaultCollection form, and
/// the ssh remote.
/// </summary>
public static partial class AzureDevOpsUrl
{
	// A project and a repository may both contain spaces, which is why the segments are matched
	// as "anything but a slash" and decoded afterwards rather than spelled out as a character
	// class. Anything past the repository - or past the pull request - is the tab the browser
	// happened to be on, and names nothing.
	[GeneratedRegex(
		@"^(?:(?:https?://)?(?:[^/@]+@)?dev\.azure\.com/(?<org>[^/]+)"
		+ @"|(?:https?://)?(?:[^/@]+@)?(?<org2>[^./]+)\.visualstudio\.com(?:/DefaultCollection)?"
		+ @"|git@ssh\.dev\.azure\.com:v3/(?<org3>[^/]+))"
		+ @"/(?<project>[^/]+)/(?:_git/)?(?<repo>[^/]+?)(?:\.git)?"
		+ @"(?:/pullrequest/(?<pr>\d+))?(?:/.*)?$",
		RegexOptions.IgnoreCase)]
	private static partial Regex Pattern();

	public static bool TryParse(string input, out string org, out string project, out string repo,
		out int? prNumber)
	{
		org = project = repo = "";
		prNumber = null;
		string text = input.Trim();
		// A copied link carries the page's state with it - "?_a=files", a discussion anchor -
		// and none of that is part of the address.
		int state = text.IndexOfAny(['#', '?']);
		if (state >= 0)
			text = text[..state];
		var match = Pattern().Match(text);
		if (!match.Success)
			return false;
		// The ssh form has no _git segment, so a three-segment path there is org/project/repo
		// and the same regex serves both.
		org = Uri.UnescapeDataString(First(match, "org", "org2", "org3"));
		project = Uri.UnescapeDataString(match.Groups["project"].Value);
		repo = Uri.UnescapeDataString(match.Groups["repo"].Value);
		if (match.Groups["pr"].Success)
			prNumber = int.Parse(match.Groups["pr"].Value);
		return org.Length > 0 && project.Length > 0 && repo.Length > 0;
	}

	static string First(Match match, params string[] names)
		=> names.Select(n => match.Groups[n]).FirstOrDefault(g => g.Success)?.Value ?? "";

	/// <summary>
	/// True when any remote of a checkout names this repository, given the output of
	/// `git config --get-regexp ^remote\..*\.url`.
	/// </summary>
	public static bool AnyRemoteMatches(string gitConfigOutput, string org, string project, string repo)
	{
		foreach (string line in gitConfigOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			int space = line.IndexOf(' ');
			if (space > 0 && RemoteMatches(line[(space + 1)..].Trim(), org, project, repo))
				return true;
		}
		return false;
	}

	/// <summary>True when a git remote URL names the same organization, project and repository.</summary>
	public static bool RemoteMatches(string remoteUrl, string org, string project, string repo)
		=> TryParse(remoteUrl, out string u, out string p, out string r, out _)
			&& string.Equals(u, org, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(p, project, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(r, repo, StringComparison.OrdinalIgnoreCase);
}
