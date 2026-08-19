using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;

using Stampeded.Core.Infra;

namespace Stampeded.Controls;

/// <summary>
/// One line of the log, with the files it names made navigable.
///
/// A log line is where a failure says which file it was about - a compiler diagnostic, a
/// stack frame, a command that ran on one file - and following it meant reading the path and
/// finding it by hand. The parts that name a file and a line are drawn as links and open the
/// file where the review shows it.
///
/// A text block rather than a row of controls: the log is monospace and column-aligned, and
/// laying a line out as several controls would space it differently from the ones around it.
/// Which link was pressed is asked of the text layout, from where the pointer went down.
/// </summary>
public sealed class LogLineBlock : TextBlock
{
	static readonly Cursor Hand = new(StandardCursorType.Hand);
	static readonly IBrush LinkBrush = Brush.Parse("#3794FF");

	public static readonly StyledProperty<string?> LineProperty =
		AvaloniaProperty.Register<LogLineBlock, string?>(nameof(Line));

	/// <summary>The line as it was logged. Set by the list's item template, and set again when
	/// the row is reused for another line.</summary>
	public string? Line {
		get => GetValue(LineProperty);
		set => SetValue(LineProperty, value);
	}

	IReadOnlyList<LogFileRef> refs = [];

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == LineProperty)
			Build(Line ?? "");
	}

	void Build(string text)
	{
		refs = LogFileRefs.Find(text);
		Inlines?.Clear();
		if (refs.Count == 0)
		{
			Text = text;
			return;
		}
		// Inlines win over Text once there are any, and the two are not kept in step: the
		// plain text has to go, or the line is drawn twice.
		Text = null;
		int at = 0;
		foreach (var reference in refs)
		{
			if (reference.Start > at)
				Inlines!.Add(new Run(text[at..reference.Start]));
			Inlines!.Add(new Run(text.Substring(reference.Start, reference.Length)) {
				Foreground = LinkBrush,
				TextDecorations = Avalonia.Media.TextDecorations.Underline,
			});
			at = reference.Start + reference.Length;
		}
		if (at < text.Length)
			Inlines!.Add(new Run(text[at..]));
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		Cursor = At(e.GetPosition(this)) is null ? null : Hand;
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		// Not handled: pressing a line also selects it, which is how it is copied, and a link
		// in it is one more thing the same press can mean.
		if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && At(e.GetPosition(this)) is { } reference)
			Open(reference);
	}

	/// <summary>The reference under a point, or null where the text says nothing about a
	/// file. Asked of the layout, so it is the character that was pressed that decides.</summary>
	LogFileRef? At(Point point)
	{
		if (refs.Count == 0)
			return null;
		var inText = point - new Point(Padding.Left, Padding.Top);
		var hit = TextLayout.HitTestPoint(inText);
		if (!hit.IsInside)
			return null;
		foreach (var reference in refs)
		{
			if (hit.TextPosition >= reference.Start && hit.TextPosition < reference.Start + reference.Length)
				return reference;
		}
		return null;
	}

	static void Open(LogFileRef reference)
	{
		if (App.Workspace is not { } workspace)
			return;
		if (RelativeTo(workspace, reference.Path) is not { } relPath)
		{
			// Every other outcome shows itself by opening something; this one would look like
			// a link that does nothing.
			CliLog.Write("action", $"log link: {reference.Path} is not in the repository or its worktrees");
			return;
		}
		workspace.NavigateToFileLineAsync(relPath, reference.Line, oldSide: false, record: true).HandleExceptions();
	}

	/// <summary>The path as the review names it. Tools log absolute paths into whichever
	/// checkout they ran in - a review worktree, the base one, the repository itself - and
	/// they all mean the same file of the same tree.</summary>
	static string? RelativeTo(ReviewWorkspace workspace, string path)
	{
		if (!Path.IsPathRooted(path))
			return path;
		foreach (string? root in new[] {
			workspace.WorktreePath, workspace.BaseWorktreePath, workspace.DirtyWorktreePath, workspace.RepoPath })
		{
			if (root is { Length: > 0 } directory && path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
				return path[(directory.Length + 1)..];
		}
		return null;
	}
}
