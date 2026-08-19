using System.Text.RegularExpressions;

namespace Stampeded.Core.Infra;

/// <summary>Where a log line names a file and a line in it: the span of the text that says
/// so, and what it says.</summary>
public readonly record struct LogFileRef(int Start, int Length, string Path, int Line);

/// <summary>
/// The file references in a line of the log, in whichever of the three forms the tools that
/// write there use: "Foo.cs(12,5)" from MSBuild, "Foo.cs:12" from git, gcc and our own
/// messages, and "Foo.cs:line 12" from a .NET stack trace.
///
/// A log line is where a failure says which file it was about, and reading it meant finding
/// that file by hand - so the text that names one is worth recognizing rather than printing.
/// </summary>
public static partial class LogFileRefs
{
	// A path is anything up to a real extension, with no whitespace and no colon in it - the
	// colon is the separator in two of the three forms and inside no path this tool prints.
	// The extension has to start with a letter, or "1.5:30" would be a file at line 30.
	[GeneratedRegex(
		"""(?<path>[^\s"'`()\[\],;:]*[^\s"'`()\[\],;:/\\]\.[A-Za-z][A-Za-z0-9]*)(?:\((?<line>\d+)(?:,\d+)?\)|:line\s+(?<line>\d+)|:(?<line>\d+)(?::\d+)?)""",
		RegexOptions.Compiled)]
	private static partial Regex Pattern();

	public static IReadOnlyList<LogFileRef> Find(string text)
	{
		List<LogFileRef>? found = null;
		foreach (Match match in Pattern().Matches(text))
		{
			var path = match.Groups["path"];
			// A URL is not a path here: what follows its host is not on this machine, and the
			// port number after it is not a line. Both are told from a file by the "//" or the
			// ":" that a path of ours never has in front of it.
			if (path.Index >= 1 && text[path.Index - 1] == ':')
				continue;
			if (path.Index >= 2 && text[(path.Index - 2)..path.Index] == "//")
				continue;
			if (!int.TryParse(match.Groups["line"].Value, out int line) || line <= 0)
				continue;
			(found ??= []).Add(new LogFileRef(match.Index, match.Length, path.Value, line));
		}
		return (IReadOnlyList<LogFileRef>?)found ?? [];
	}
}
