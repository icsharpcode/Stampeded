using Avalonia.Controls;
using Avalonia.Input;

using AvaloniaEdit.Rendering;

using Stampeded.Core.Diff;

namespace Stampeded.Documents;

/// <summary>One remark in a thread: posted or drafted, and what can be done with it.</summary>
sealed record ThreadComment(bool IsDraft, string Author, string Body, Guid? DraftId, string? ThreadId = null,
	bool Resolved = false, string? Url = null, long CommentId = 0);

/// <summary>Everything said about one line of one side of a file.</summary>
sealed record ThreadData(bool OldSide, int BlobLine, List<ThreadComment> Comments, string? OutdatedQuote = null,
	bool Approximate = false);

/// <summary>
/// What has been said about one file, keyed the way the document marker lines name it.
///
/// Read here rather than in a view, because both layouts show the same threads: a comment
/// belongs to a line of a blob, which is a fact about the review and not about how it is
/// being drawn.
/// </summary>
static class CommentThreads
{
	/// <summary>The threads of one file: the posted comments and this pass's drafts, grouped
	/// by the line they hang on. Ones whose line is gone are keyed apart and pinned at the top
	/// of the file by the caller, since there is no line left to sit under.</summary>
	public static Dictionary<string, ThreadData> For(ReviewWorkspace workspace, FileDiff file)
	{
		var threads = new Dictionary<string, ThreadData>();
		void Add(string path, bool oldSide, int? blobLine, bool isDraft, string author, string body, Guid? draftId,
			bool approximate = false, string? threadId = null, bool resolved = false, string? url = null,
			long commentId = 0)
		{
			string expected = oldSide ? file.OldPath : file.Path;
			if (blobLine is not { } line || path != expected)
				return;
			string key = $"{(oldSide ? "o" : "n")}{line}";
			if (!threads.TryGetValue(key, out var thread))
				threads[key] = thread = new ThreadData(oldSide, line, [], Approximate: approximate);
			else if (approximate && !thread.Approximate)
				threads[key] = thread = thread with { Approximate = true, Comments = thread.Comments };
			thread.Comments.Add(new ThreadComment(isDraft, author, body, draftId, threadId, resolved, url, commentId));
		}
		foreach (var posted in workspace.Comments.Posted)
			Add(posted.RelPath, posted.OldSide, posted.Line, false, posted.Author, posted.Body, null,
				posted.IsApproximate, posted.ThreadId, posted.IsResolved, posted.Url, posted.CommentId);
		foreach (var draft in workspace.Comments.Drafts)
			Add(draft.Stored.Anchor.Path, draft.Stored.Anchor.OldSide, draft.CurrentLine, true, "you (draft)",
				draft.Stored.Body, draft.Stored.Id, draft.IsApproximate);

		// Comments whose location no longer exists are pinned at the top of the file, marked
		// outdated and quoting the code they originally hung on.
		int outdatedIndex = 0;
		foreach (var posted in workspace.Comments.Posted.Where(p => p.Line is null && !p.OldSide
			&& p.RelPath == file.Path))
		{
			threads[$"od{outdatedIndex++}"] = new ThreadData(false, 0,
				[new ThreadComment(false, posted.Author, posted.Body, null)]);
		}
		foreach (var draft in workspace.Comments.Drafts.Where(d => d.CurrentLine is null
			&& d.Stored.Anchor.Path == (d.Stored.Anchor.OldSide ? file.OldPath : file.Path)))
		{
			threads[$"od{outdatedIndex++}"] = new ThreadData(false, 0,
				[new ThreadComment(true, "you (draft)", draft.Stored.Body, draft.Stored.Id)],
				draft.Stored.Anchor.LineText);
		}
		return threads;
	}

	/// <summary>The anchors of those threads, in the order they are spliced into a
	/// document.</summary>
	public static List<ThreadAnchor> Anchors(Dictionary<string, ThreadData> threads)
		=> [.. threads
			.OrderBy(t => t.Value.BlobLine)
			.Select(t => new ThreadAnchor(t.Value.OldSide, t.Value.BlobLine, t.Key))];
}

/// <summary>
/// Draws one comment thread as a box inside an editor: the remarks, what is outdated about
/// them, and the buttons that act on them.
///
/// One per editor, because the box is sized to the view it hangs in and the "show me the
/// resolved thread again" state belongs to what the reader is looking at. Replying and
/// editing a draft are the view's business - both open its comment editor - so they arrive
/// as callbacks rather than being done here.
/// </summary>
sealed class CommentThreadBox(
	TextView view,
	Action<Guid, string, ThreadData> onEditDraft,
	Action<ThreadData, long> onReply)
{
	static readonly global::Markdown.Avalonia.Markdown ThreadMarkdownEngine = Editor.MarkdownLinks.NewEngine();

	/// <summary>Resolved threads the reader has opened again, by key; cleared with the
	/// document, since the keys name lines of it.</summary>
	readonly HashSet<string> openedResolvedThreads = [];

	public void Forget() => openedResolvedThreads.Clear();

	/// <summary>The box for one thread, or the one-line summary when it is resolved and the
	/// reader has not asked to see it again.</summary>
	public Avalonia.Controls.Control Build(string key, ThreadData thread)
	{
		bool dark = Themes.ThemeManager.Current.IsDarkTheme;
		var panel = new Avalonia.Controls.StackPanel { Spacing = 4 };
		if (thread.BlobLine == 0 || thread.Approximate)
		{
			string banner = thread.BlobLine == 0
				? "OUTDATED - the commented code is gone from this head"
				: "OUTDATED - approximate location (the exact line is gone)";
			panel.Children.Add(new Avalonia.Controls.TextBlock {
				Text = banner + (thread.OutdatedQuote is { Length: > 0 } quote ? $"; was: {quote.Trim()}" : ""),
				FontSize = 11,
				FontStyle = Avalonia.Media.FontStyle.Italic,
				Opacity = 0.75,
				TextWrapping = Avalonia.Media.TextWrapping.Wrap,
			});
		}
		bool resolvedThread = thread.Comments.Count > 0 && thread.Comments.All(c => c.DraftId is not null || c.Resolved)
			&& thread.Comments.Any(c => c.Resolved);
		// A resolved thread is settled business: it takes a line instead of a box, and opens
		// again on demand. One holding an unsent draft stays open - hiding the reader's own
		// unposted words would be losing them.
		if (resolvedThread && !openedResolvedThreads.Contains(key)
			&& !thread.Comments.Any(c => c.DraftId is not null))
		{
			return BuildResolvedSummary(key, thread, dark);
		}
		foreach (var comment in thread.Comments)
		{
			var header = new Avalonia.Controls.DockPanel();
			if (comment.Url is { Length: > 0 } commentUrl)
			{
				var github = new Avalonia.Controls.Button {
					Content = "GitHub",
					FontSize = 10,
					Padding = new Avalonia.Thickness(5, 1),
					Cursor = new Cursor(StandardCursorType.Hand),
					[Avalonia.Controls.DockPanel.DockProperty] = Avalonia.Controls.Dock.Right,
				};
				github.Click += (_, _) => App.Workspace?.OpenUrlAsync(commentUrl).HandleExceptions();
				header.Children.Add(github);
			}
			if (comment.DraftId is { } draftId)
			{
				var delete = new Avalonia.Controls.Button {
					Content = "Delete draft",
					FontSize = 10,
					Padding = new Avalonia.Thickness(5, 1),
					[Avalonia.Controls.DockPanel.DockProperty] = Avalonia.Controls.Dock.Right,
				};
				delete.Click += (_, _) => App.Workspace?.Comments.RemoveDraft(draftId);
				header.Children.Add(delete);
				var edit = new Avalonia.Controls.Button {
					Content = "Edit",
					FontSize = 10,
					Padding = new Avalonia.Thickness(5, 1),
					[Avalonia.Controls.DockPanel.DockProperty] = Avalonia.Controls.Dock.Right,
				};
				string draftBody = comment.Body;
				edit.Click += (_, _) => onEditDraft(draftId, draftBody, thread);
				header.Children.Add(edit);
			}
			header.Children.Add(new Avalonia.Controls.TextBlock {
				Text = comment.Author,
				FontWeight = Avalonia.Media.FontWeight.SemiBold,
				FontSize = 12,
				Foreground = comment.IsDraft
					? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D29922"))
					: (dark ? Avalonia.Media.Brushes.Gainsboro : Avalonia.Media.Brushes.Black),
			});
			panel.Children.Add(header);
			// Review comments are markdown (code spans, lists, links). Rendered via the
			// engine directly: a ScrollViewer inside an editor inline object would nest
			// scroll regions into every visual line and wreck scrolling performance.
			var rendered = ThreadMarkdownEngine.Transform(
			Core.GitHub.IssueLinks.Autolink(comment.Body, App.Workspace?.IssueUrlPrefix));
			// A remark about `Foo` written in bold is bold and about `Foo`; the renderer draws
			// one of the two and the markers of the other.
			Controls.MarkdownEmphasis.Repair(rendered);
			var host = new Avalonia.Controls.ContentControl {
				Content = rendered,
				Margin = new Avalonia.Thickness(0, 0, 0, 4),
			};
			host.Styles.Add(global::Markdown.Avalonia.MarkdownStyle.Standard);
			// The engine's default paragraph spacing is cramped for review prose.
			var paragraphStyle = new Avalonia.Styling.Style(x =>
				Avalonia.Styling.Selectors.OfType(x, typeof(global::ColorTextBlock.Avalonia.CTextBlock)));
			paragraphStyle.Setters.Add(new Avalonia.Styling.Setter(
				Avalonia.Layout.Layoutable.MarginProperty, new Avalonia.Thickness(0, 0, 0, 10)));
			host.Styles.Add(paragraphStyle);
			panel.Children.Add(host);
		}
		var buttons = new Avalonia.Controls.StackPanel {
			Orientation = Avalonia.Layout.Orientation.Horizontal,
			Spacing = 6,
		};
		// The thread is answered through the id of a comment already in it; without one there
		// is nothing to reply to, only a new remark on the same line, which is what "Comment
		// Here" is for.
		long replyTo = thread.Comments.FirstOrDefault(c => c.CommentId != 0)?.CommentId ?? 0;
		if (thread.BlobLine > 0 && !thread.Approximate && replyTo != 0)
		{
			var reply = new Avalonia.Controls.Button { Content = "Reply", FontSize = 10, Padding = new Avalonia.Thickness(6, 1) };
			reply.Click += (_, _) => onReply(thread, replyTo);
			buttons.Children.Add(reply);
		}
		if (thread.Comments.FirstOrDefault(c => c.ThreadId is not null)?.ThreadId is { } gitThreadId)
		{
			var toggle = new Avalonia.Controls.Button {
				Content = resolvedThread ? "Unresolve" : "Resolve",
				FontSize = 10,
				Padding = new Avalonia.Thickness(6, 1),
			};
			toggle.Click += (_, _) => App.Workspace?.Comments.SetThreadResolvedAsync(gitThreadId, !resolvedThread).HandleExceptions();
			buttons.Children.Add(toggle);
		}
		if (resolvedThread)
		{
			var hide = new Avalonia.Controls.Button { Content = "Hide", FontSize = 10, Padding = new Avalonia.Thickness(6, 1) };
			hide.Click += (_, _) => {
				openedResolvedThreads.Remove(key);
				view.Redraw();
			};
			buttons.Children.Add(hide);
		}
		if (buttons.Children.Count > 0)
			panel.Children.Add(buttons);
		if (resolvedThread)
		{
			panel.Children.Insert(0, new Avalonia.Controls.TextBlock {
				Text = "Resolved",
				FontSize = 10,
				FontWeight = Avalonia.Media.FontWeight.SemiBold,
				Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2EA043")),
			});
		}
		foreach (var button in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(panel).OfType<Avalonia.Controls.Button>())
			button.Cursor = new Cursor(StandardCursorType.Hand);
		var border = new Avalonia.Controls.Border {
			// The editor's I-beam must not bleed over the embedded box; children without
			// their own cursor inherit the arrow from here.
			Cursor = new Cursor(StandardCursorType.Arrow),
			// Prose, not code: the box is an inline object inside the editor and would
			// otherwise inherit its monospace family. What is written between backticks keeps
			// the monospace family the markdown style gives it.
			[Avalonia.Controls.Documents.TextElement.FontFamilyProperty] = Avalonia.Media.FontFamily.Default,
			Opacity = resolvedThread ? 0.55 : 1.0,
			Child = panel,
			Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(dark ? "#2B2417" : "#FFF8C5"), dark ? 0.9 : 0.9),
			BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D2992255")),
			BorderThickness = new Avalonia.Thickness(1),
			CornerRadius = new Avalonia.CornerRadius(4),
			Padding = new Avalonia.Thickness(10, 6),
			Margin = new Avalonia.Thickness(24, 2, 8, 2),
			HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
		};
		// Full viewport width (an inline object only sizes to content otherwise), kept
		// in sync with view resizes; the subscription dies with the box.
		var widthSubscription = Avalonia.AvaloniaObjectExtensions
			.GetObservable(view, Avalonia.Visual.BoundsProperty)
			.Subscribe(new Avalonia.Reactive.AnonymousObserver<Avalonia.Rect>(bounds =>
				border.Width = Math.Max(320, bounds.Width - 56)));
		border.DetachedFromVisualTree += (_, _) => widthSubscription.Dispose();
		return border;
	}

	/// <summary>
	/// A resolved thread in one row: who said it, how much there is, and a way back to it.
	/// The full box is what an open question deserves; a settled one only has to stay
	/// findable.
	/// </summary>
	Avalonia.Controls.Control BuildResolvedSummary(string key, ThreadData thread, bool dark)
	{
		var first = thread.Comments[0];
		string excerpt = first.Body.ReplaceLineEndings(" ").Trim();
		if (excerpt.Length > 80)
			excerpt = excerpt[..80] + "...";
		var row = new Avalonia.Controls.StackPanel {
			Orientation = Avalonia.Layout.Orientation.Horizontal,
			Spacing = 6,
			VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
		};
		row.Children.Add(new Avalonia.Controls.TextBlock {
			Text = "Resolved",
			FontSize = 10,
			FontWeight = Avalonia.Media.FontWeight.SemiBold,
			Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#2EA043")),
			VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
		});
		row.Children.Add(new Avalonia.Controls.TextBlock {
			Text = $"{first.Author}: {excerpt}"
				+ (thread.Comments.Count > 1 ? $"  (+{thread.Comments.Count - 1})" : ""),
			FontSize = 11,
			Opacity = 0.75,
			TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
			VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
		});
		var show = new Avalonia.Controls.Button {
			Content = "Show",
			FontSize = 10,
			Padding = new Avalonia.Thickness(6, 0),
			Cursor = new Cursor(StandardCursorType.Hand),
		};
		show.Click += (_, _) => {
			openedResolvedThreads.Add(key);
			view.Redraw();
		};
		row.Children.Add(show);
		return new Avalonia.Controls.Border {
			Cursor = new Cursor(StandardCursorType.Arrow),
			[Avalonia.Controls.Documents.TextElement.FontFamilyProperty] = Avalonia.Media.FontFamily.Default,
			Opacity = 0.55,
			Child = row,
			Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(dark ? "#2B2417" : "#FFF8C5"), 0.9),
			BorderBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D2992255")),
			BorderThickness = new Avalonia.Thickness(1),
			CornerRadius = new Avalonia.CornerRadius(4),
			Padding = new Avalonia.Thickness(10, 2),
			Margin = new Avalonia.Thickness(0, 1),
		};
	}
}

/// <summary>
/// How tall one thread's row is, shared by the two panes of a side-by-side view.
///
/// The panes are kept in step by copying one scroll offset to the other, which only holds
/// while both draw the same rows at the same heights. A thread belongs to one side, so the
/// other side draws nothing there - and has to draw nothing of exactly the right height.
/// </summary>
sealed class ThreadRowHeight
{
	IDisposable? subscription;

	public double Height { get; private set; }

	public event Action? Changed;

	/// <summary>Follows the box the owning pane built. A later rebuild hands over a new box;
	/// the old subscription goes with it.</summary>
	public void Track(Control box)
	{
		subscription?.Dispose();
		subscription = Avalonia.AvaloniaObjectExtensions
			.GetObservable(box, Avalonia.Visual.BoundsProperty)
			.Subscribe(new Avalonia.Reactive.AnonymousObserver<Avalonia.Rect>(bounds => {
				if (Math.Abs(bounds.Height - Height) < 0.5)
					return;
				Height = bounds.Height;
				Changed?.Invoke();
			}));
	}
}

/// <summary>The other pane's half of a thread row: nothing to read, exactly as tall as the
/// box across from it.</summary>
sealed class ThreadSpacer : Control
{
	readonly ThreadRowHeight row;
	readonly TextView view;

	public ThreadSpacer(ThreadRowHeight row, TextView view)
	{
		this.row = row;
		this.view = view;
		row.Changed += OnChanged;
		DetachedFromVisualTree += (_, _) => row.Changed -= OnChanged;
	}

	/// <summary>The editor measures an inline object once, when it builds the visual line it
	/// sits in, and keeps that height until the line is built again - so asking for a new
	/// measurement is not enough on its own. The redraw builds the line again, which measures
	/// this spacer against the height the box has now; it settles after one pass, because the
	/// height only reports a change when it really changed.</summary>
	void OnChanged()
	{
		InvalidateMeasure();
		view.Redraw();
	}

	protected override Avalonia.Size MeasureOverride(Avalonia.Size availableSize)
		=> new(1, row.Height);
}
