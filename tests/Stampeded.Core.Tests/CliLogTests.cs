using NUnit.Framework;

using Stampeded.Core.Infra;

namespace Stampeded.Core.Tests;

/// <summary>
/// The log has to survive the window not being there yet. A windowed run on Windows has no
/// console behind it, so a line that was written before the pane existed and not kept is a
/// line nobody can ever read - which is exactly the case a language server that fails to
/// start falls into.
/// </summary>
public class CliLogTests
{
	[Test]
	public void ASinkThatArrivesLateIsToldWhatItMissed()
	{
		var previous = CliLog.Sink;
		try
		{
			CliLog.Sink = null;
			string marker = "written before there was anywhere to show it";
			CliLog.Write("test", marker);

			var seen = new List<string>();
			CliLog.Sink = seen.Add;

			Assert.That(seen, Has.Some.Contains(marker), "the backlog is replayed into a new sink");

			CliLog.Write("test", "and after");
			Assert.That(seen[^1], Does.Contain("and after"), "and it keeps receiving");
		}
		finally
		{
			CliLog.Sink = previous;
		}
	}
}
