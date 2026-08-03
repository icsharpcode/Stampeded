using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;

namespace Stampeded;

/// <summary>
/// Tracks concurrent long-running activities and animates a braille spinner while any
/// are active. Begin() from any thread; UI-bound properties update on the UI thread.
/// </summary>
public sealed partial class BusyTracker : ObservableObject
{
	static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

	readonly object gate = new();
	readonly Dictionary<Guid, string> active = [];
	readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
	int frame;

	[ObservableProperty]
	string text = "";

	[ObservableProperty]
	bool isBusy;

	[ObservableProperty]
	string spinnerFrame = Frames[0];

	public BusyTracker()
	{
		timer.Tick += (_, _) => SpinnerFrame = Frames[frame = (frame + 1) % Frames.Length];
	}

	public IDisposable Begin(string label)
	{
		var id = Guid.NewGuid();
		lock (gate)
			active[id] = label;
		Publish();
		return new Scope(this, id);
	}

	void Remove(Guid id)
	{
		lock (gate)
			active.Remove(id);
		Publish();
	}

	void Publish()
	{
		Dispatcher.UIThread.Post(() => {
			string text;
			bool busy;
			lock (gate)
			{
				busy = active.Count > 0;
				text = string.Join("  ·  ", active.Values);
			}
			Text = text;
			IsBusy = busy;
			if (busy)
				timer.Start();
			else
				timer.Stop();
		});
	}

	sealed class Scope(BusyTracker owner, Guid id) : IDisposable
	{
		public void Dispose() => owner.Remove(id);
	}
}
