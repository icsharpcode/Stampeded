using System.Collections.ObjectModel;

using Avalonia.Threading;
using Avalonia.Media;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Infra;

namespace Stampeded.Panes;

/// <summary>
/// Live log of external commands (git / gh / dotnet, with exit codes and timing), language
/// servers and workspace actions. Capped ring buffer; newest at the bottom. This is the only
/// place any of it is visible: the console the same lines go to is not there at all in a
/// windowed run on Windows.
/// </summary>
public partial class LogPaneViewModel : Tool
{
	const int MaxLines = 2000;
	static readonly IBrush ErrorBrush = Brush.Parse("#F85149");
	static readonly IBrush WarningBrush = Brush.Parse("#D29922");

	public ObservableCollection<string> Lines { get; } = [];
	public ObservableCollection<LogLineRow> VisibleLines { get; } = [];

	[ObservableProperty]
	string filterText = "";

	public string CountText => FilterText.Trim().Length == 0
		? $"{Lines.Count} entries"
		: $"{VisibleLines.Count} of {Lines.Count} entries";

	public LogPaneViewModel()
	{
		// Setting the sink replays what was written before this pane existed - the start of
		// this run, and everything logged under a repository opened earlier in it.
		CliLog.Sink = line => Dispatcher.UIThread.Post(() => Append(line));
	}

	void Append(string line)
	{
		Lines.Add(line);
		if (Matches(line))
			VisibleLines.Add(LogLineRow.From(line));
		while (Lines.Count > MaxLines)
		{
			string removed = Lines[0];
			Lines.RemoveAt(0);
			if (VisibleLines.Count > 0 && VisibleLines[0].Text == removed)
				VisibleLines.RemoveAt(0);
			else if (FilterText.Trim().Length > 0)
				Refilter();
		}
		OnPropertyChanged(nameof(CountText));
	}

	public void Clear()
	{
		Lines.Clear();
		VisibleLines.Clear();
		OnPropertyChanged(nameof(CountText));
	}

	partial void OnFilterTextChanged(string value) => Refilter();

	void Refilter()
	{
		VisibleLines.Clear();
		foreach (string line in Lines.Where(Matches))
			VisibleLines.Add(LogLineRow.From(line));
		OnPropertyChanged(nameof(CountText));
	}

	bool Matches(string line)
	{
		string text = FilterText.Trim();
		return text.Length == 0 || line.Contains(text, StringComparison.OrdinalIgnoreCase);
	}

	public static IBrush? BrushFor(string line)
	{
		string text = line.ToUpperInvariant();
		if (text.Contains("FAILED", StringComparison.Ordinal)
			|| text.Contains("ERROR", StringComparison.Ordinal)
			|| text.Contains("EXCEPTION", StringComparison.Ordinal)
			|| HasNonZeroExit(text))
			return ErrorBrush;
		if (text.Contains("WARNING", StringComparison.Ordinal)
			|| text.Contains("SKIPPED", StringComparison.Ordinal))
			return WarningBrush;
		return null;
	}

	static bool HasNonZeroExit(string text)
	{
		const string marker = "-> EXIT ";
		int at = text.IndexOf(marker, StringComparison.Ordinal);
		if (at < 0)
			return false;
		int start = at + marker.Length;
		int end = start;
		if (end < text.Length && text[end] == '-')
			end++;
		while (end < text.Length && char.IsDigit(text[end]))
			end++;
		return int.TryParse(text[start..end], out int exitCode) && exitCode != 0;
	}
}

public sealed class LogLineRow : System.ComponentModel.INotifyPropertyChanged
{
	public string Text { get; }
	public string Summary { get; }
	public string Details { get; }
	public IBrush? Brush { get; }
	public bool HasDetails => Details.Length > 0;
	public string ToggleLabel => IsExpanded ? "▾" : "▸";
	public bool DetailsVisible => IsExpanded;

	bool isExpanded;
	public bool IsExpanded
	{
		get => isExpanded;
		private set
		{
			if (isExpanded == value)
				return;
			isExpanded = value;
			PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
			PropertyChanged?.Invoke(this, new(nameof(ToggleLabel)));
			PropertyChanged?.Invoke(this, new(nameof(DetailsVisible)));
		}
	}

	LogLineRow(string text)
	{
		Text = text;
		int newline = text.IndexOf('\n');
		Summary = newline < 0 ? text : text[..newline];
		Details = newline < 0 ? "" : text[(newline + 1)..].TrimEnd();
		Brush = LogPaneViewModel.BrushFor(Summary);
	}

	public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
	public void ToggleDetails() => IsExpanded = !IsExpanded;

	public static LogLineRow From(string text) => new(text);
}
