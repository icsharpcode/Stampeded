using System.Collections.ObjectModel;

using Avalonia.Threading;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Panes;

/// <summary>
/// Live log of external commands (git / gh / dotnet, with exit codes and timing) and
/// workspace actions. Capped ring buffer; newest at the bottom.
/// </summary>
public class LogPaneViewModel : Tool
{
	const int MaxLines = 2000;

	public ObservableCollection<string> Lines { get; } = [];

	public LogPaneViewModel()
	{
		CliLog.Sink = line => Dispatcher.UIThread.Post(() => Append(line));
		CliLog.Write("app", "log started");
	}

	void Append(string line)
	{
		Lines.Add(line);
		while (Lines.Count > MaxLines)
			Lines.RemoveAt(0);
	}

	public void Clear() => Lines.Clear();
}
