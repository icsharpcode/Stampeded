using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

using Stampeded.Core.Diff;
using Stampeded.Core.Roslyn;

namespace Stampeded;

/// <summary>Where a "Go to" ended: a path of the repository, and a line in it. Either can
/// stand alone - a path without a line opens the file, a line without a path moves within the
/// document in front.</summary>
public sealed record GoToTarget(string? Path, int? Line);

/// <summary>What a row of the dialog offers, and where it came from.</summary>
public enum GoToKind
{
	ChangedFile,
	Symbol,
	RepositoryFile,
}

/// <summary>A row of the Go to list.</summary>
public sealed class GoToRow(GoToKind kind, string path, int? line, string title, string detail, string badge)
{
	public GoToKind Kind { get; } = kind;

	public string Path { get; } = path;

	public int? Line { get; } = line;

	/// <summary>What the row is called: a file's path, or a symbol's name.</summary>
	public string Title { get; } = title;

	/// <summary>Where it lives: the containing type for a symbol, nothing for a file.</summary>
	public string Detail { get; } = detail;

	/// <summary>The letter or word in front: the change kind of a file, the kind of a symbol.</summary>
	public string Badge { get; } = badge;

	public string LineTag => Line is { } l ? $":{l.ToString(CultureInfo.InvariantCulture)}" : "";

	public IBrush BadgeBrush => Kind switch {
		GoToKind.ChangedFile => Changed,
		GoToKind.Symbol => SymbolColor,
		_ => Muted,
	};

	static readonly IBrush Changed = new SolidColorBrush(Color.Parse("#2EA043"));
	static readonly IBrush SymbolColor = new SolidColorBrush(Color.Parse("#3794FF"));
	static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8B949E"));
}

/// <summary>
/// One box for everything "go to" can mean: a file of the change, a file of the repository, a
/// declaration by name, and a line in any of them.
///
/// What is typed before a colon searches, what follows it is the line, and a bare number is a
/// line in the document in front - no path is spelled with digits alone. The three kinds of
/// answer are ranked rather than separated: the change being read comes first, then the
/// symbols matching, then the rest of the repository, because that is the order in which a
/// reader means them.
///
/// Symbols come from Roslyn's own pattern matcher, so "RWS" finds RoslynWorkspaceService the
/// way an IDE would. They arrive behind the files: the solution is large, the query runs
/// against every keystroke, and a box that waits for it is a box that stutters.
/// </summary>
public partial class GoToWindow : Window
{
	readonly ReviewWorkspace? workspace;
	readonly string? currentPath;
	readonly HashSet<string> changedPaths = new(StringComparer.Ordinal);
	readonly DispatcherTimer symbolDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
	IReadOnlyList<string> repositoryFiles = [];
	CancellationTokenSource? symbolSearch;
	string symbolsFor = "";
	IReadOnlyList<DeclarationHit> symbols = [];

	/// <summary>How many rows of each kind are worth showing. A list nobody scrolls to the end
	/// of is a list that only costs time to build.</summary>
	const int PerKindLimit = 40;

	public ObservableCollection<GoToRow> Rows { get; } = [];

	public GoToWindow()
	{
		InitializeComponent();
		DataContext = this;
	}

	public GoToWindow(ReviewWorkspace workspace, string? currentPath) : this()
	{
		this.workspace = workspace;
		this.currentPath = currentPath;
		foreach (var file in workspace.Files)
			changedPaths.Add(file.Path);
		symbolDebounce.Tick += (_, _) => {
			symbolDebounce.Stop();
			SearchSymbolsAsync().HandleExceptions();
		};
		InputBox.TextChanged += (_, _) => {
			Refresh();
			symbolDebounce.Stop();
			symbolDebounce.Start();
		};
		Opened += (_, _) => {
			InputBox.Focus();
			Refresh();
			LoadRepositoryFilesAsync().HandleExceptions();
		};
		Closed += (_, _) => symbolSearch?.Cancel();
	}

	async Task LoadRepositoryFilesAsync()
	{
		if (workspace is null)
			return;
		repositoryFiles = await workspace.ListHeadFilesAsync();
		Refresh();
	}

	/// <summary>Runs the symbol query for what is typed now, cancelling whatever the previous
	/// keystroke started.</summary>
	async Task SearchSymbolsAsync()
	{
		if (workspace is null)
			return;
		var (filter, _) = Split(InputBox.Text ?? "");
		if (filter.Length < 2)
		{
			symbols = [];
			symbolsFor = filter;
			Refresh();
			return;
		}
		symbolSearch?.Cancel();
		var cts = new CancellationTokenSource();
		symbolSearch = cts;
		try
		{
			var hits = await workspace.FindDeclarationsAsync(filter, PerKindLimit, cts.Token);
			if (cts.IsCancellationRequested)
				return;
			symbols = hits;
			symbolsFor = filter;
			Refresh();
		}
		catch (OperationCanceledException)
		{
		}
	}

	void Refresh()
	{
		var (filter, line) = Split(InputBox.Text ?? "");
		int selected = Math.Max(0, Rows.Count > 0 ? Matches.SelectedIndex : 0);
		Rows.Clear();

		// The change first, and within it the file in front: a bare line number is a move
		// inside what is being read, which is the common case.
		foreach (var file in Changed(filter).Take(PerKindLimit))
			Rows.Add(new GoToRow(GoToKind.ChangedFile, file.Path, line, file.Path, "", Marker(file)));

		// Symbols only carry their own line: the number typed is about a file.
		if (symbolsFor == filter)
		{
			foreach (var hit in symbols)
				Rows.Add(new GoToRow(GoToKind.Symbol, hit.RelPath, hit.Line, hit.Name, Where(hit), hit.Kind.ToLowerInvariant()));
		}

		foreach (var path in repositoryFiles.Where(p => !changedPaths.Contains(p)))
		{
			if (Rows.Count >= PerKindLimit * 3)
				break;
			if (WordFilter.Matches(filter, path))
				Rows.Add(new GoToRow(GoToKind.RepositoryFile, path, line, path, "", ""));
		}

		if (Rows.Count > 0)
			Matches.SelectedIndex = Math.Min(selected, Rows.Count - 1);
		HintText.Text = Hint(filter, line);
	}

	IEnumerable<FileDiff> Changed(string filter)
	{
		if (workspace is null)
			return [];
		return workspace.Files
			.Where(f => WordFilter.Matches(filter, f.Path))
			.OrderBy(f => f.Path == currentPath ? 0 : 1);
	}

	static string Where(DeclarationHit hit)
		=> hit.Container.Length > 0 ? $"{hit.Container}  -  {hit.RelPath}" : hit.RelPath;

	static string Marker(FileDiff file) => file.Kind switch {
		FileChangeKind.Added => "A",
		FileChangeKind.Deleted => "D",
		FileChangeKind.Renamed => "R",
		_ => "M",
	};

	string Hint(string filter, int? line)
	{
		if (Rows.Count == 0)
		{
			return filter.Length > 0
				? $"Nothing matches '{filter}'."
				: "Type a file, a symbol, or a line number.";
		}
		if (line is { } l && filter.Length == 0 && currentPath is not null)
			return $"Enter goes to line {l} of {currentPath}; pick another file to go to line {l} there.";
		return workspace is { SemanticsReady: false }
			? "Enter opens what is selected. Symbols appear once semantics have loaded."
			: "Enter opens what is selected. Add ':' and a number to land on a line.";
	}

	/// <summary>
	/// The words to search for and the line to land on. Numbers are peeled off the end, so a
	/// completed entry that already carries one and then gets another - which is what typing a
	/// line after Tab does - means the number just typed, not a path nobody has.
	/// </summary>
	static (string Filter, int? Line) Split(string text)
	{
		string rest = text.Trim();
		int? line = null;
		while (true)
		{
			int colon = rest.LastIndexOf(':');
			if (colon < 0
				|| !int.TryParse(rest[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number))
			{
				break;
			}
			line ??= number > 0 ? number : null;
			rest = rest[..colon].TrimEnd();
		}
		// A bare number is a line, not something to look for: no path is spelled with digits
		// alone.
		return line is null && int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out int only)
			? ("", only > 0 ? only : null)
			: (rest, line);
	}

	/// <summary>Escape closes with nothing, wherever focus sits - the box, the list, or a row
	/// being clicked.</summary>
	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.Handled || e.Key != Key.Escape)
			return;
		Close(null);
		e.Handled = true;
	}

	void OnInputKeyDown(object? sender, KeyEventArgs e)
	{
		switch (e.Key)
		{
			// The list is driven from the box, which never loses focus: a reader typing a name
			// and stepping to the second match should not have to reach for the mouse.
			case Key.Down:
				Step(1);
				break;
			case Key.Up:
				Step(-1);
				break;
			case Key.PageDown:
				Step(10);
				break;
			case Key.PageUp:
				Step(-10);
				break;
			case Key.Tab:
				Complete();
				break;
			case Key.Enter:
				Accept();
				break;
			default:
				return;
		}
		e.Handled = true;
	}

	/// <summary>Writes what is selected into the box as the place it stands for, so what
	/// follows can be typed against it. A member expands to the file and the line it is
	/// declared on, which is both what the row was offering and a number the reader can now
	/// edit; a file keeps whatever line was already typed after it.</summary>
	void Complete()
	{
		if (Matches.SelectedItem is not GoToRow row)
			return;
		InputBox.Text = row.Line is { } line
			? $"{row.Path}:{line.ToString(CultureInfo.InvariantCulture)}"
			: row.Path;
		InputBox.CaretIndex = InputBox.Text.Length;
	}

	void Step(int delta)
	{
		if (Rows.Count == 0)
			return;
		int index = Math.Clamp(Matches.SelectedIndex + delta, 0, Rows.Count - 1);
		Matches.SelectedIndex = index;
		Matches.ScrollIntoView(index);
	}

	void OnMatchDoubleTapped(object? sender, TappedEventArgs e) => Accept();

	void Accept()
	{
		var (filter, line) = Split(InputBox.Text ?? "");
		// A line on its own belongs to the document in front, which is not in the list at all
		// when it is a decompiled tab or a source view rather than a file of the change.
		if (filter.Length == 0 && line is not null)
		{
			Close(new GoToTarget(null, line));
			return;
		}
		if (Matches.SelectedItem is not GoToRow row)
			return;
		// A symbol knows where it is; the number typed alongside a file is the reader's.
		Close(new GoToTarget(row.Path, row.Kind == GoToKind.Symbol ? row.Line : line));
	}
}
