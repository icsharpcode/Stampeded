using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

using Stampeded.Core.Diff;

namespace Stampeded;

/// <summary>Where a "Go to" ended: a file of the review, a line, or both.</summary>
public sealed record GoToTarget(FileDiff? File, int? Line);

/// <summary>A changed file as the dialog lists it.</summary>
public sealed class GoToRow(FileDiff file, int? line)
{
	public FileDiff File { get; } = file;

	public string Path => File.Path;

	public string Kind => File.Kind switch {
		FileChangeKind.Added => "A",
		FileChangeKind.Deleted => "D",
		FileChangeKind.Renamed => "R",
		_ => "M",
	};

	public IBrush KindBrush => File.Kind switch {
		FileChangeKind.Added => Added,
		FileChangeKind.Deleted => Removed,
		_ => Other,
	};

	/// <summary>The line the dialog would land on, shown on every row so the number typed is
	/// visibly part of the choice rather than something that happens afterwards.</summary>
	public string LineTag => line is { } l ? $":{l.ToString(CultureInfo.InvariantCulture)}" : "";

	static readonly IBrush Added = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush Removed = new SolidColorBrush(Color.Parse("#F85149"));
	static readonly IBrush Other = new SolidColorBrush(Color.Parse("#8B949E"));
}

/// <summary>
/// One box for the two things a reader means by "go to": a file of the review, and a line in
/// it. What is typed before a colon filters the changed files by word, what follows it is the
/// line - so "thememan:120" and "120" and "thememan" are all sentences this understands, the
/// bare number meaning the file already in front.
///
/// Closes with the choice, or null when nothing was picked.
/// </summary>
public partial class GoToWindow : Window
{
	readonly IReadOnlyList<FileDiff> allFiles;
	readonly string? currentPath;

	public ObservableCollection<GoToRow> Files { get; } = [];

	public GoToWindow()
	{
		InitializeComponent();
		allFiles = [];
		DataContext = this;
	}

	public GoToWindow(IReadOnlyList<FileDiff> files, string? currentPath) : this()
	{
		allFiles = files;
		this.currentPath = currentPath;
		InputBox.TextChanged += (_, _) => Refresh();
		Opened += (_, _) => {
			InputBox.Focus();
			Refresh();
		};
	}

	void Refresh()
	{
		var (filter, line) = Split(InputBox.Text ?? "");
		Files.Clear();
		// With nothing typed for the file, the file in front leads: a bare line number is a
		// move inside what is being read, and that is the common case.
		foreach (var file in allFiles.OrderBy(f => f.Path == currentPath ? 0 : 1))
		{
			if (WordFilter.Matches(filter, file.Path))
				Files.Add(new GoToRow(file, line));
		}
		if (Matches.SelectedIndex < 0 && Files.Count > 0)
			Matches.SelectedIndex = 0;
		HintText.Text = Hint(filter, line);
	}

	string Hint(string filter, int? line)
	{
		if (allFiles.Count == 0)
			return "No review is open, so there are no files to go to. A line number moves within the document in front.";
		if (Files.Count == 0)
			return filter.Length > 0 ? $"No changed file matches '{filter}'." : "";
		return line is { } l && filter.Length == 0 && currentPath is not null
			? $"Enter goes to line {l} of {currentPath}; pick another file to go to line {l} there."
			: "Enter opens the selected file. Add ':' and a number to land on a line.";
	}

	/// <summary>The file words and the line, split at the last colon so a path holding one -
	/// which a Windows-style path can - still filters.</summary>
	static (string Filter, int? Line) Split(string text)
	{
		string trimmed = text.Trim();
		int colon = trimmed.LastIndexOf(':');
		if (colon >= 0
			&& int.TryParse(trimmed[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int after))
		{
			return (trimmed[..colon].Trim(), after > 0 ? after : null);
		}
		// A bare number is a line, not a file to look for: no path is spelled with digits alone.
		return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int only)
			? ("", only > 0 ? only : null)
			: (trimmed, null);
	}

	void OnInputKeyDown(object? sender, KeyEventArgs e)
	{
		switch (e.Key)
		{
			// The list is driven from the box, which never loses focus: a reader typing a name
			// and stepping to the second match should not have to reach for the mouse or Tab.
			case Key.Down:
				Step(1);
				break;
			case Key.Up:
				Step(-1);
				break;
			case Key.Enter:
				Accept();
				break;
			default:
				return;
		}
		e.Handled = true;
	}

	void Step(int delta)
	{
		if (Files.Count == 0)
			return;
		int index = Math.Clamp(Matches.SelectedIndex + delta, 0, Files.Count - 1);
		Matches.SelectedIndex = index;
		Matches.ScrollIntoView(index);
	}

	void OnMatchDoubleTapped(object? sender, TappedEventArgs e) => Accept();

	void Accept()
	{
		var (filter, line) = Split(InputBox.Text ?? "");
		// A line on its own belongs to the document in front, which is not in the list at all
		// when no review is open - a decompiled tab, a source view, the keyboard-shortcut page.
		var file = filter.Length == 0 && line is not null
			? null
			: (Matches.SelectedItem as GoToRow)?.File;
		if (file is null && line is null)
			return;
		Close(new GoToTarget(file, line));
	}
}
