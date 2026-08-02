using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Stampeded.Core.Testing;

public enum TestOutcome
{
	Passed,
	Failed,
	Skipped,
	Other,
}

public sealed partial record TestResult(
	string TestName,
	TestOutcome Outcome,
	TimeSpan Duration,
	string? ErrorMessage,
	string? StackTrace)
{
	[GeneratedRegex(@" in (?<file>.+?):line (?<line>\d+)")]
	private static partial Regex StackFrameLocation();

	/// <summary>First "in file:line N" frame of the stack trace, if any.</summary>
	public (string FilePath, int Line)? TryGetSourceLocation()
	{
		if (StackTrace is null)
			return null;
		var match = StackFrameLocation().Match(StackTrace);
		if (!match.Success)
			return null;
		return (match.Groups["file"].Value, int.Parse(match.Groups["line"].Value));
	}
}

/// <summary>Parses VSTest/MTP .trx result files.</summary>
public static class TrxParser
{
	static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

	public static IReadOnlyList<TestResult> Parse(string trxContent)
	{
		var doc = XDocument.Parse(trxContent);
		var results = new List<TestResult>();
		foreach (var element in doc.Descendants(Ns + "UnitTestResult"))
		{
			string name = (string?)element.Attribute("testName") ?? "";
			var outcome = ((string?)element.Attribute("outcome")) switch {
				"Passed" => TestOutcome.Passed,
				"Failed" => TestOutcome.Failed,
				"NotExecuted" or "Skipped" => TestOutcome.Skipped,
				_ => TestOutcome.Other,
			};
			TimeSpan.TryParse((string?)element.Attribute("duration"), out var duration);
			var errorInfo = element.Element(Ns + "Output")?.Element(Ns + "ErrorInfo");
			results.Add(new TestResult(
				name, outcome, duration,
				errorInfo?.Element(Ns + "Message")?.Value,
				errorInfo?.Element(Ns + "StackTrace")?.Value));
		}
		return results;
	}
}
