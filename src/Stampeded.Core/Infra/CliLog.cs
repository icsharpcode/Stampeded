namespace Stampeded.Core.Infra;

/// <summary>
/// Process-wide log sink for external commands and user-visible actions. The UI sets
/// <see cref="Sink"/> once; writers are on arbitrary threads, so the sink must marshal.
/// </summary>
public static class CliLog
{
	public static Action<string>? Sink { get; set; }

	public static void Write(string category, string message)
		=> Sink?.Invoke($"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}");
}
