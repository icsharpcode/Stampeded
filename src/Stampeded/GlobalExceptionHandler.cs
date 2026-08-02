using System.Diagnostics;

namespace Stampeded;

/// <summary>
/// Central sink for exceptions escaping fire-and-forget tasks and event handlers.
/// Minimal for now: trace and debugger-break; grows a user-visible error dialog once
/// the app has a window service to host it.
/// </summary>
public static class GlobalExceptionHandler
{
	public static void Show(Exception exception)
	{
		Trace.TraceError("Unhandled exception: {0}", exception);
		if (Debugger.IsAttached)
			Debugger.Break();
	}
}
