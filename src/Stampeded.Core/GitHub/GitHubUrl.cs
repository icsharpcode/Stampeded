using System.Text.RegularExpressions;

namespace Stampeded.Core.GitHub;

/// <summary>
/// Parses GitHub repository and pull-request URLs (https, ssh, or bare owner/repo) into
/// owner, repository name, and an optional PR number.
/// </summary>
public static partial class GitHubUrl
{
	// Anything after the repository - or after the pull request - is where the browser
	// happened to be looking: the files tab, a commit, a review thread. It names no
	// repository, so it is matched and discarded rather than refused.
	[GeneratedRegex(
		@"^(?:(?:https?://)?(?:www\.)?github\.com[:/]|git@github\.com:)?(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?(?:/pull/(?<pr>\d+))?(?:/.*)?$")]
	private static partial Regex Pattern();

	public static bool TryParse(string input, out string owner, out string repo, out int? prNumber)
	{
		owner = repo = "";
		prNumber = null;
		string text = input.Trim();
		// A copied link carries the page's state with it - "#discussion_r123", "?w=1" - and
		// none of that is part of the address.
		int state = text.IndexOfAny(['#', '?']);
		if (state >= 0)
			text = text[..state];
		var match = Pattern().Match(text);
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
