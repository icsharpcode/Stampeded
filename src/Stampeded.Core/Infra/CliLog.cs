namespace Stampeded.Core.Infra;

/// <summary>
/// Process-wide log sink for external commands and user-visible actions. The UI sets
/// <see cref="Sink"/> once; writers are on arbitrary threads, so the sink must marshal.
///
/// What was written before there was a sink is kept and handed to the sink that arrives:
/// the window is built after the workspace it shows, and on Windows there is no console
/// behind it to fall back on, so a line written early would otherwise exist nowhere at all.
/// The same replay is what carries the log across a switch to another repository, which
/// builds a new layout and with it a new pane.
/// </summary>
public static class CliLog
{
	/// <summary>As many lines as the pane itself keeps; a longer memory here would only be
	/// dropped on the way in.</summary>
	const int Backlog = 2000;

	static readonly Lock gate = new();
	static readonly Queue<string> history = new();
	static Action<string>? sink;

	public static Action<string>? Sink
	{
		get { lock (gate) return sink; }
		set {
			lock (gate)
			{
				sink = value;
				if (value is null)
					return;
				// Inside the lock, so a line written meanwhile queues behind the replay
				// instead of arriving in the middle of it or twice.
				foreach (string line in history)
					value(line);
			}
		}
	}

	public static void Write(string category, string message)
	{
		string line = $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}";
		Console.WriteLine(line); // mirrors to any captured stdout for headless debugging
		Action<string>? current;
		lock (gate)
		{
			history.Enqueue(line);
			while (history.Count > Backlog)
				history.Dequeue();
			current = sink;
		}
		current?.Invoke(line);
	}
}
