using System.Text.RegularExpressions;

namespace Stampeded.Core.GitHub;

/// <summary>
/// Parses GitHub repository and pull-request URLs (https, ssh, or bare owner/repo) into
/// owner, repository name, and an optional PR number.
/// </summary>
public static partial class GitHubUrl
{
	[GeneratedRegex(
		@"^(?:(?:https?://)?(?:www\.)?github\.com[:/]|git@github\.com:)?(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?(?:/pull/(?<pr>\d+)(?:/[a-z]*)?)?/?$")]
	private static partial Regex Pattern();

	public static bool TryParse(string input, out string owner, out string repo, out int? prNumber)
	{
		owner = repo = "";
		prNumber = null;
		var match = Pattern().Match(input.Trim());
		if (!match.Success)
			return false;
		owner = match.Groups["owner"].Value;
		repo = match.Groups["repo"].Value;
		if (match.Groups["pr"].Success)
			prNumber = int.Parse(match.Groups["pr"].Value);
		return true;
	}

	/// <summary>True when a git remote URL names the same owner/repo.</summary>
	public static bool RemoteMatches(string remoteUrl, string owner, string repo)
		=> TryParse(remoteUrl, out string remoteOwner, out string remoteRepo, out _)
			&& string.Equals(remoteOwner, owner, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(remoteRepo, repo, StringComparison.OrdinalIgnoreCase);
}
