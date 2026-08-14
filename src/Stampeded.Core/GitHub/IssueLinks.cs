using System.Text.RegularExpressions;

namespace Stampeded.Core.GitHub;

/// <summary>
/// Turns "#1234" into a link, the way GitHub renders it everywhere its own text appears.
/// A review is full of them - a pull request body naming what it fixes, a comment pointing
/// at the issue that explains why - and as plain text each one is a number to go and look up
/// by hand.
/// </summary>
public static partial class IssueLinks
{
	// The skip alternatives come first, so a "#123" inside them is consumed as part of the
	// thing it belongs to and never rewritten: fenced code, an inline span, a link that is
	// already a link, and a bare URL (whose fragment can look exactly like a reference).
	[GeneratedRegex(
		"""(?<skip>```[\s\S]*?```|`[^`\n]*`|!?\[[^\]]*\]\([^)]*\)|<[^>\s]+>|https?://\S+)|(?<![\w/#])\#(?<issue>\d+)\b""",
		RegexOptions.Compiled)]
	private static partial Regex Pattern();

	/// <summary>
	/// Rewrites plain issue references as markdown links under <paramref name="issueUrlPrefix"/>
	/// (".../issues/", which GitHub redirects to the pull request when the number is one).
	/// The text is returned untouched when there is no repository to point at.
	/// </summary>
	public static string Autolink(string markdown, string? issueUrlPrefix)
	{
		if (markdown.Length == 0 || string.IsNullOrEmpty(issueUrlPrefix) || !markdown.Contains('#'))
			return markdown;
		return Pattern().Replace(markdown, match => match.Groups["issue"].Success
			? $"[#{match.Groups["issue"].Value}]({issueUrlPrefix}{match.Groups["issue"].Value})"
			: match.Value);
	}
}
