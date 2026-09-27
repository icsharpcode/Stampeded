using System.Text.RegularExpressions;

namespace Stampeded.Core.Bitbucket;

/// <summary>
/// Parses Bitbucket Data Center repository and pull-request URLs. Data Center instances are
/// self-hosted, sometimes below a context path, so the base URL is whatever precedes
/// /projects/... or /scm/....
/// </summary>
public static partial class BitbucketUrl
{
	[GeneratedRegex(
		@"^(?<base>https?://.+?)/(?:projects/(?<project>[^/]+)/repos/(?<repo>[^/]+)|scm/(?<project2>[^/]+)/(?<repo2>[^/]+?)(?:\.git)?)"
		+ @"(?:/pull-requests/(?<pr>\d+))?(?:/.*)?$",
		RegexOptions.IgnoreCase)]
	private static partial Regex HttpPattern();

	[GeneratedRegex(
		@"^(?:ssh://)?git@(?<host>[^/:]+)(?::(?<port>\d+))?[/:](?<project>[^/]+)/(?<repo>[^/]+?)(?:\.git)?$",
		RegexOptions.IgnoreCase)]
	private static partial Regex SshPattern();

	public static bool TryParse(string input, out string baseUrl, out string projectKey, out string repo,
		out int? prNumber)
	{
		baseUrl = projectKey = repo = "";
		prNumber = null;
		string text = input.Trim();
		int state = text.IndexOfAny(['#', '?']);
		if (state >= 0)
			text = text[..state];

		var http = HttpPattern().Match(text);
		if (http.Success)
		{
			baseUrl = http.Groups["base"].Value.TrimEnd('/');
			projectKey = Uri.UnescapeDataString(First(http, "project", "project2"));
			repo = Uri.UnescapeDataString(First(http, "repo", "repo2"));
			if (http.Groups["pr"].Success)
				prNumber = int.Parse(http.Groups["pr"].Value);
			return baseUrl.Length > 0 && projectKey.Length > 0 && repo.Length > 0;
		}

		var ssh = SshPattern().Match(text);
		if (!ssh.Success)
			return false;
		baseUrl = $"https://{ssh.Groups["host"].Value}";
		projectKey = Uri.UnescapeDataString(ssh.Groups["project"].Value);
		repo = Uri.UnescapeDataString(ssh.Groups["repo"].Value);
		return projectKey.Length > 0 && repo.Length > 0;
	}

	static string First(Match match, params string[] names)
		=> names.Select(n => match.Groups[n]).FirstOrDefault(g => g.Success)?.Value ?? "";

	public static bool AnyRemoteMatches(string gitConfigOutput, string baseUrl, string projectKey, string repo)
	{
		foreach (string line in gitConfigOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			int space = line.IndexOf(' ');
			if (space > 0 && RemoteMatches(line[(space + 1)..].Trim(), baseUrl, projectKey, repo))
				return true;
		}
		return false;
	}

	public static bool RemoteMatches(string remoteUrl, string baseUrl, string projectKey, string repo)
		=> TryParse(remoteUrl, out string b, out string p, out string r, out _)
			&& SameBase(b, baseUrl)
			&& string.Equals(p, projectKey, StringComparison.OrdinalIgnoreCase)
			&& string.Equals(r, repo, StringComparison.OrdinalIgnoreCase);

	static bool SameBase(string a, string b)
		=> string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
			|| string.Equals(HostOnly(a), HostOnly(b), StringComparison.OrdinalIgnoreCase);

	static string HostOnly(string url)
		=> Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

	public static string HttpCloneUrl(string baseUrl, string projectKey, string repo)
		=> $"{baseUrl.TrimEnd('/')}/scm/{Uri.EscapeDataString(projectKey)}/{Uri.EscapeDataString(repo)}.git";

	public static string CloneUrlFor(string input, string baseUrl, string projectKey, string repo)
	{
		string text = input.Trim();
		int state = text.IndexOfAny(['#', '?']);
		if (state >= 0)
			text = text[..state];
		return SshPattern().IsMatch(text) || text.Contains("/scm/", StringComparison.OrdinalIgnoreCase)
			? text
			: HttpCloneUrl(baseUrl, projectKey, repo);
	}
}
