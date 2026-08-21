using System.Collections.ObjectModel;

using Avalonia.Threading;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Panes;

/// <summary>
/// Live log of external commands (git / gh / dotnet, with exit codes and timing), language
/// servers and workspace actions. Capped ring buffer; newest at the bottom. This is the only
/// place any of it is visible: the console the same lines go to is not there at all in a
/// windowed run on Windows.
/// </summary>
public class LogPaneViewModel : Tool
{
	const int MaxLines = 2000;

	public ObservableCollection<string> Lines { get; } = [];

	public LogPaneViewModel()
	{
		// Setting the sink replays what was written before this pane existed - the start of
		// this run, and everything logged under a repository opened earlier in it.
		CliLog.Sink = line => Dispatcher.UIThread.Post(() => Append(line));
	}

	void Append(string line)
	{
		Lines.Add(line);
		while (Lines.Count > MaxLines)
			Lines.RemoveAt(0);
	}

	public void Clear() => Lines.Clear();
}
