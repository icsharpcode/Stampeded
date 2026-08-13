using System.Diagnostics;

using Stampeded.Core.Infra;

namespace Stampeded;

/// <summary>
/// Central sink for exceptions escaping fire-and-forget tasks and event handlers.
/// </summary>
public static class GlobalExceptionHandler
{
	public static void Show(Exception exception)
	{
		Trace.TraceError("Unhandled exception: {0}", exception);
		// The Log pane is where a user looks when a command did nothing. Tracing alone left a
		// failed command indistinguishable from one that found nothing to do: no status, no
		// log line, the pane sitting at whatever it last said.
		CliLog.Write("error", $"{exception.GetType().Name}: {exception.Message}"
			+ (FirstFrame(exception) is { } frame ? $"  at {frame}" : ""));
		if (Debugger.IsAttached)
			Debugger.Break();
	}

	/// <summary>The topmost stack line, which says where it broke without printing a page.</summary>
	static string? FirstFrame(Exception exception)
	{
		string? line = exception.StackTrace?.Split('\n', 2)[0].Trim();
		return line is not null && line.StartsWith("at ", StringComparison.Ordinal) ? line[3..] : line;
	}
}
