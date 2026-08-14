using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.Search;

using Stampeded.Core.Diff;
using Stampeded.Core.Roslyn;
using Stampeded.Diff;
using Stampeded.Editor;

namespace Stampeded.Documents;

/// <summary>Payload of a clickable reference span: which side and blob position it names.</summary>
sealed record TokenRef(bool OldSide, int Line, int Column);

public partial class DiffDocumentView : UserControl
{
	static readonly Color OccurrenceColor = Color.Parse("#5A86C691");
	static readonly Color DefinitionOccurrenceColor = Color.Parse("#7A86C691");

	readonly DiffLineNumberMargin margin = new();
	readonly CoverageMargin coverageMargin = new();
	bool coverageMarginVisible;
	readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
	readonly ReferenceElementGenerator referenceGenerator = new(_ => true);
	readonly TextMarkerService markers;
	readonly Editor.ThreadElementGenerator threadGenerator = new();
	Dictionary<string, ThreadData>? threadsByKey;
	/// <summary>Resolved threads the reader has opened again; they collapse by default.</summary>
	readonly HashSet<string> openedResolvedThreads = [];
	Avalonia.Point lastPointerPosition;
	FoldingManager? foldingManager;
	ContextGapView? contextGaps;
	List<FoldRange> structuralRanges = [];
	DiffDocumentModel? model;
	DiffDocumentViewModel? viewModel;
	RichTextColorizer? semanticColorizer;
	bool semanticsRefreshQueued;

	public DiffDocumentView()
	{
		InitializeComponent();
		SearchPanel.Install(Editor);
		Editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => model?.Tags));
		markers = new TextMarkerService(Editor.TextArea.TextView);
		Editor.TextArea.TextView.BackgroundRenderers.Add(markers);
		threadGenerator.ControlFactory = BuildThreadControl;
		Editor.TextArea.TextView.ElementGenerators.Add(threadGenerator);
		Editor.TextArea.TextView.ElementGenerators.Add(referenceGenerator);
		// Hand cursor only while Ctrl is held, matching the Ctrl+Click navigation gesture
		// (a permanent hand over every identifier promises plain-click navigation we
		// deliberately don't do - plain click places the caret / highlights occurrences).
		referenceGenerator.QueryCursor = (element, segment, modifiers) =>
			element.Cursor = new Cursor(
				modifiers.HasFlag(KeyModifiers.Control) && segment.Kind == ReferenceMode.Link
					? StandardCursorType.Hand
					: StandardCursorType.Ibeam);
		Editor.TextArea.LeftMargins.Insert(0, margin);
		FoldViewportAnchor.Install(Editor);
		contextGaps = new ContextGapView(Editor);
		contextGaps.Changed += RefreshFoldings;
		Editor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
		// Click-vs-drag discrimination (ported from ILSpy's DecompilerTextView): the press
		// only records its position; the release compares against it, so press-and-drag
		// over a link selects text instead of navigating away on release.
		Editor.TextArea.AddHandler(PointerPressedEvent, OnTextAreaPointerPressedForClick, RoutingStrategies.Tunnel, handledEventsToo: true);
		// On the TextArea, not the TextView: AvaloniaEdit captures the pointer on press,
		// and captured releases are raised on the capturing control - a TextView handler
		// never sees them (evidenced by presses logging without releases).
		Editor.TextArea.AddHandler(PointerReleasedEvent, OnTextViewPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
		Editor.TextArea.AddHandler(PointerPressedEvent, OnPointerPressedForContextMenu, RoutingStrategies.Tunnel);
		AddHandler(GotFocusEvent, (_, _) => MakeActive(), RoutingStrategies.Bubble, handledEventsToo: true);
		Editor.TextArea.TextView.PointerMoved += OnPointerMovedForHover;
		Editor.TextArea.TextView.PointerExited += (_, _) => CancelHover();
		hoverTimer.Tick += OnHoverTimerTick;
		blameMargin.CommitRequested = blame =>
			App.Workspace?.OpenHistoricalDiffAsync(blame.Sha, viewModel?.File.Path ?? "").HandleExceptions();
	}

	/// <summary>The most recently attached/focused diff view; menu commands route here.</summary>
	public static DiffDocumentView? ActiveView { get; private set; }

	/// <summary>Raised when the active diff view (or its document) changes; the History
	/// pane follows it.</summary>
	public static event Action? ActiveViewChanged;

	internal DiffDocumentViewModel? ViewModel => viewModel;

	void MakeActive()
	{
		ActiveView = this;
		ActiveViewChanged?.Invoke();
	}

	/// <summary>
	/// Puts keyboard focus in the text area. The single-key review gestures (v, n, p, [, ],
	/// c, b, u) are a handler on it, so they are dead until it holds focus - which opening a
	/// document does not give it: Dock's focused dockable is a layout concept, not the
	/// keyboard's.
	/// </summary>
	public void FocusEditor() => Editor.TextArea.Focus();

	/// <summary>
	/// The view showing a given document, for code that has the document and needs the
	/// control. <see cref="ActiveView"/> cannot answer this: Dock keeps every document's view
	/// attached and only swaps which one is visible, so "last attached or focused" is stale
	/// the moment a tab is selected without the mouse.
	/// </summary>
	public static DiffDocumentView? ViewFor(DiffDocumentViewModel document)
		=> viewsByDocument.TryGetValue(document, out var view) ? view : null;

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DiffDocumentViewModel, DiffDocumentView>
		viewsByDocument = new();

	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		MakeActive();
		if (App.Workspace is { } ws)
		{
			ws.SemanticsChanged += OnSemanticsChanged;
			ws.CoverageChanged += OnCoverageChanged;
			ws.CommentsChanged += OnCommentsChangedForThreads;
		}
		Themes.ThemeManager.Current.ThemeChanged += OnThemeChangedForSemantics;
		QueueSemanticsRefresh();
		OnCoverageChanged();
		OnCommentsChangedForThreads();
	}

	protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		if (ActiveView == this)
			ActiveView = null;
		if (App.Workspace is { } ws)
		{
			ws.SemanticsChanged -= OnSemanticsChanged;
			ws.CoverageChanged -= OnCoverageChanged;
			ws.CommentsChanged -= OnCommentsChangedForThreads;
		}
		Themes.ThemeManager.Current.ThemeChanged -= OnThemeChangedForSemantics;
		base.OnDetachedFromVisualTree(e);
	}


	#region Menu / context-menu commands

	public void JumpToHunkCommand(int direction) => JumpToHunk(direction);

	static readonly global::Markdown.Avalonia.Markdown ThreadMarkdownEngine = MarkdownLinks.NewEngine();

	ReviewWorkspace.CommentTarget? inlineCommentTarget;

	public void CommentAtCaretCommand() => CommentAtCaretCommand(null);

	/// <param name="anchorLine">Document line the editor should be placed under. A reply
	/// belongs below the thread it answers, and the thread's box sits on its own line under
	/// the commented code - anchoring to the caret alone would open the editor on top of
	/// what is being replied to.</param>
	public void CommentAtCaretCommand(int? anchorLine)
	{
		if (viewModel is { Historical: true } || CaretBlobPosition() is not { } pos)
			return;
		if (App.Workspace is { CanComment: false } local)
		{
			// Say it here rather than let the popup take text that BeginComment would drop.
			local.PostStatus("Comments need a pull request; this is a local review.");
			return;
		}
		var docLine = Editor.Document.GetLineByNumber(Editor.TextArea.Caret.Line);
		string text = Editor.Document.GetText(docLine.Offset, docLine.Length);
		inlineCommentTarget = new ReviewWorkspace.CommentTarget(pos.RelPath, pos.OldSide, pos.Line, text);
		CommentTargetText.Text = $"{pos.RelPath}:{pos.Line}{(pos.OldSide ? " (base)" : "")}  |  {text.Trim()}";
		var view = Editor.TextArea.TextView;
		int anchorAt = Math.Clamp(anchorLine ?? Editor.TextArea.Caret.Line, 1, Editor.Document.LineCount);
		var caretPosition = new AvaloniaEdit.TextViewPosition(anchorAt, 1);
		double anchorY = ScrollToMakeRoomBelow(caretPosition);
		double marginsWidth = Editor.TextArea.LeftMargins.OfType<Avalonia.Controls.Control>().Sum(m => m.Bounds.Width);
		CommentPopup.HorizontalOffset = marginsWidth + 8;
		CommentPopup.VerticalOffset = anchorY;
		CommentPopup.IsOpen = true;
		CommentBox.Focus();
	}

	/// <summary>
	/// The offset the editor box should sit at, having scrolled far enough that it fits under
	/// its anchor. Replying to a tall thread otherwise puts the box past the bottom of the
	/// diff, where it floats over the pane below - the popup is an overlay and knows nothing
	/// of the editor's bounds.
	/// </summary>
	double ScrollToMakeRoomBelow(AvaloniaEdit.TextViewPosition position)
	{
		var view = Editor.TextArea.TextView;
		double anchorY = (view.GetVisualPosition(position, VisualYPosition.LineBottom) - view.ScrollOffset).Y;
		double overflow = anchorY + CommentBoxHeight - view.Bounds.Height;
		if (overflow > 0
			&& Editor.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().FirstOrDefault() is { } scroll)
		{
			double max = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
			double target = Math.Clamp(scroll.Offset.Y + overflow, 0, max);
			double moved = target - scroll.Offset.Y;
			scroll.Offset = new Avalonia.Vector(scroll.Offset.X, target);
			anchorY -= moved;
		}
		return anchorY;
	}

	/// <summary>Height the comment editor needs, as laid out in the view's markup.</summary>
	const double CommentBoxHeight = 150;

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
			Editor.TextArea.TextView.Redraw();
		};
		row.Children.Add(show);
		return new Avalonia.Controls.Border {
			Cursor = new Cursor(StandardCursorType.Arrow),
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

	/// <summary>
	/// The last of the thread rows reserved under a code line, or the line itself when it
	/// carries none. Threads are spliced in as synthetic lines below the code they comment
	/// on, so this is the bottom of everything already said there.
	/// </summary>
	int LastThreadLineAfter(int docLine)
	{
		if (model is null)
			return docLine;
		int last = docLine;
		for (int line = docLine + 1; line <= model.Tags.Count && model.Tags[line - 1].Kind == DiffLineKind.Comment; line++)
			last = line;
		return last;
	}

	sealed record ThreadComment(bool IsDraft, string Author, string Body, Guid? DraftId, string? ThreadId = null, bool Resolved = false, string? Url = null);

	sealed record ThreadData(bool OldSide, int BlobLine, List<ThreadComment> Comments, string? OutdatedQuote = null, bool Approximate = false);

	void OnCommentsChangedForThreads()
	{
		Dispatcher.UIThread.Post(RebuildThreads);
	}

	/// <summary>Recomputes the comment threads of this file and re-splices the document
	/// with a reserved line per thread; caret and view position are restored via the
	/// blob mapping, which survives the reflow.</summary>
	void RebuildThreads()
	{
		if (viewModel is null || viewModel.Historical || App.Workspace is not { } ws)
			return;
		var threads = new Dictionary<string, ThreadData>();
		void Add(string path, bool oldSide, int? blobLine, bool isDraft, string author, string body, Guid? draftId,
			bool approximate = false, string? threadId = null, bool resolved = false, string? url = null)
		{
			string expected = oldSide ? viewModel!.File.OldPath : viewModel!.File.Path;
			if (blobLine is not { } line || path != expected)
				return;
			string key = $"{(oldSide ? "o" : "n")}{line}";
			if (!threads.TryGetValue(key, out var thread))
				threads[key] = thread = new ThreadData(oldSide, line, [], Approximate: approximate);
			else if (approximate && !thread.Approximate)
				threads[key] = thread = thread with { Approximate = true, Comments = thread.Comments };
			thread.Comments.Add(new ThreadComment(isDraft, author, body, draftId, threadId, resolved, url));
		}
		foreach (var posted in ws.PostedComments)
			Add(posted.RelPath, posted.OldSide, posted.Line, false, posted.Author, posted.Body, null,
				posted.IsApproximate, posted.ThreadId, posted.IsResolved, posted.Url);
		foreach (var draft in ws.Drafts)
			Add(draft.Stored.Anchor.Path, draft.Stored.Anchor.OldSide, draft.CurrentLine, true, "you (draft)", draft.Stored.Body, draft.Stored.Id,
				draft.IsApproximate);

		// Comments whose location no longer exists are pinned at the top of the file,
		// marked outdated and quoting the code they originally hung on.
		int outdatedIndex = 0;
		foreach (var posted in ws.PostedComments.Where(p => p.Line is null && !p.OldSide
			&& p.RelPath == viewModel.File.Path))
		{
			threads[$"od{outdatedIndex++}"] = new ThreadData(false, 0,
				[new ThreadComment(false, posted.Author, posted.Body, null)]);
		}
		foreach (var draft in ws.Drafts.Where(d => d.CurrentLine is null
			&& d.Stored.Anchor.Path == (d.Stored.Anchor.OldSide ? viewModel.File.OldPath : viewModel.File.Path)))
		{
			threads[$"od{outdatedIndex++}"] = new ThreadData(false, 0,
				[new ThreadComment(true, "you (draft)", draft.Stored.Body, draft.Stored.Id)],
				draft.Stored.Anchor.LineText);
		}

		threadsByKey = threads.Count == 0 ? null : threads;
		var anchors = threads
			.OrderBy(t => t.Value.BlobLine)
			.Select(t => new Core.Diff.ThreadAnchor(t.Value.OldSide, t.Value.BlobLine, t.Key))
			.ToList();
		var target = anchors.Count == 0
			? viewModel.PristineModel
			: viewModel.PristineModel.WithThreadLines(anchors);
		if (ReferenceEquals(target, model) || target.Text == model?.Text)
		{
			Editor.TextArea.TextView.Redraw();
			return;
		}
		var caret = CaretBlobPosition();
		var expandedFolds = CaptureExpandedFolds();
		var openedGaps = CaptureGaps();
		viewModel.ReplaceModel(target);
		model = target;
		ApplyModelToEditor(target);
		RestoreExpandedFolds(expandedFolds);
		RestoreGaps(openedGaps, target);
		if (caret is { } restore)
		{
			// Restore position without focusing: a background tab grabbing focus would
			// make the dock activate it (e.g. stealing the front from the Overview).
			int? docLine = restore.OldSide ? target.DocLineFromOldLine(restore.Line) : target.DocLineFromNewLine(restore.Line);
			if (docLine is { } dl && dl >= 1 && dl <= Editor.Document.LineCount)
			{
				int offset = Editor.Document.GetLineByNumber(dl).Offset;
				if (foldingManager is not null)
				{
					foreach (var folding in foldingManager.GetFoldingsContaining(offset))
						folding.IsFolded = false;
				}
				Editor.TextArea.Caret.Line = dl;
				Editor.TextArea.Caret.Column = 1;
				Editor.ScrollToLine(dl);
			}
		}
	}

	void ApplyMarginCursors()
	{
		foreach (var marginControl in Editor.TextArea.LeftMargins.OfType<Avalonia.Controls.Control>())
			marginControl.Cursor = new Cursor(StandardCursorType.Arrow);
	}

	/// <summary>Blob positions (side, line) of folds the user has expanded; fold state is
	/// keyed by content so it survives the re-splices that renumber document lines.</summary>
	List<(bool OldSide, int Line)> CaptureExpandedFolds()
	{
		var expanded = new List<(bool, int)>();
		if (foldingManager is null || model is null)
			return expanded;
		foreach (var folding in foldingManager.AllFoldings.Where(f => !f.IsFolded))
		{
			int docLine = Editor.Document.GetLineByOffset(folding.StartOffset).LineNumber;
			if (docLine < 1 || docLine > model.Tags.Count)
				continue;
			var tag = model.Tags[docLine - 1];
			if (tag.NewLine > 0)
				expanded.Add((false, tag.NewLine));
			else if (tag.OldLine > 0)
				expanded.Add((true, tag.OldLine));
		}
		return expanded;
	}

	void RestoreExpandedFolds(List<(bool OldSide, int Line)> expanded)
	{
		if (foldingManager is null || model is null || expanded.Count == 0)
			return;
		foreach (var (oldSide, blobLine) in expanded)
		{
			int? docLine = oldSide ? model.DocLineFromOldLine(blobLine) : model.DocLineFromNewLine(blobLine);
			if (docLine is not { } dl || dl < 1 || dl > Editor.Document.LineCount)
				continue;
			int offset = Editor.Document.GetLineByNumber(dl).Offset;
			foreach (var folding in foldingManager.AllFoldings.Where(f => f.StartOffset == offset))
				folding.IsFolded = false;
		}
	}

	/// <summary>
	/// What each gap still hides, as blob positions. Splicing a comment thread into the
	/// document renumbers every line below it, so how far the reader has opened the context
	/// has to be carried by content rather than by line number - the same reason fold state
	/// is.
	/// </summary>
	List<((bool OldSide, int Line) First, (bool OldSide, int Line) Last)> CaptureGaps()
	{
		var carried = new List<((bool, int), (bool, int))>();
		if (contextGaps is null || model is null)
			return carried;
		foreach (var gap in contextGaps.Gaps)
		{
			if (BlobPosition(gap.FirstLine) is { } first && BlobPosition(gap.LastLine) is { } last)
				carried.Add((first, last));
		}
		return carried;

		(bool OldSide, int Line)? BlobPosition(int docLine)
		{
			if (docLine < 1 || docLine > model.Tags.Count)
				return null;
			var tag = model.Tags[docLine - 1];
			return tag.NewLine > 0 ? (false, tag.NewLine) : tag.OldLine > 0 ? (true, tag.OldLine) : null;
		}
	}

	void RestoreGaps(
		List<((bool OldSide, int Line) First, (bool OldSide, int Line) Last)> carried, DiffDocumentModel m)
	{
		if (contextGaps is null || carried.Count == 0)
			return;
		var gaps = new List<ContextGap>();
		foreach (var (first, last) in carried)
		{
			if (DocLine(first) is { } firstDoc && DocLine(last) is { } lastDoc && lastDoc >= firstDoc)
				gaps.Add(new ContextGap(firstDoc, lastDoc));
		}
		contextGaps.Restore(gaps);

		int? DocLine((bool OldSide, int Line) position)
			=> position.OldSide ? m.DocLineFromOldLine(position.Line) : m.DocLineFromNewLine(position.Line);
	}

	void ApplyModelToEditor(DiffDocumentModel m)
	{
		Editor.Text = m.Text;
		margin.Tags = m.Tags;
		margin.InvalidateMeasure();
		Overview.Attach(Editor, m.Tags);
		InstallFoldsAndGaps(m);
		ApplyMarginCursors();
		referenceGenerator.References = null;
		markers.RemoveAll(_ => true);
		QueueSemanticsRefresh();
	}

	Avalonia.Controls.Control? BuildThreadControl(string key)
	{
		if (threadsByKey is null || !threadsByKey.TryGetValue(key, out var thread))
			return null;
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
				delete.Click += (_, _) => App.Workspace?.RemoveDraft(draftId);
				header.Children.Add(delete);
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
		if (thread.BlobLine > 0 && !thread.Approximate)
		{
			var reply = new Avalonia.Controls.Button { Content = "Reply", FontSize = 10, Padding = new Avalonia.Thickness(6, 1) };
			reply.Click += (_, _) => {
				int? docLine = thread.OldSide ? model?.DocLineFromOldLine(thread.BlobLine) : model?.DocLineFromNewLine(thread.BlobLine);
				if (docLine is { } dl)
				{
					MoveCaretToLine(dl);
					CommentAtCaretCommand(LastThreadLineAfter(dl));
				}
			};
			buttons.Children.Add(reply);
		}
		if (thread.Comments.FirstOrDefault(c => c.ThreadId is not null)?.ThreadId is { } gitThreadId)
		{
			var toggle = new Avalonia.Controls.Button {
				Content = resolvedThread ? "Unresolve" : "Resolve",
				FontSize = 10,
				Padding = new Avalonia.Thickness(6, 1),
			};
			toggle.Click += (_, _) => App.Workspace?.SetThreadResolvedAsync(gitThreadId, !resolvedThread).HandleExceptions();
			buttons.Children.Add(toggle);
		}
		if (resolvedThread)
		{
			var hide = new Avalonia.Controls.Button { Content = "Hide", FontSize = 10, Padding = new Avalonia.Thickness(6, 1) };
			hide.Click += (_, _) => {
				openedResolvedThreads.Remove(key);
				Editor.TextArea.TextView.Redraw();
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
		var view = Editor.TextArea.TextView;
		var widthSubscription = Avalonia.AvaloniaObjectExtensions
			.GetObservable(view, Avalonia.Visual.BoundsProperty)
			.Subscribe(new Avalonia.Reactive.AnonymousObserver<Avalonia.Rect>(bounds =>
				border.Width = Math.Max(320, bounds.Width - 56)));
		border.DetachedFromVisualTree += (_, _) => widthSubscription.Dispose();
		return border;
	}

	void OnCommentBoxKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
		{
			e.Handled = true;
			OnCommentSave(sender, e);
		}
		else if (e.Key == Key.Escape)
		{
			e.Handled = true;
			OnCommentCancel(sender, e);
		}
	}

	void OnCommentSave(object? sender, RoutedEventArgs e) => SaveInlineCommentAsync().HandleExceptions();

	async Task SaveInlineCommentAsync()
	{
		if (inlineCommentTarget is not { } target || App.Workspace is not { } ws)
			return;
		string body = CommentBox.Text?.Trim() ?? "";
		if (body.Length == 0)
			return;
		ws.BeginComment(target, activatePane: false);
		await ws.CommitDraftAsync(body);
		CommentBox.Text = "";
		CommentPopup.IsOpen = false;
		inlineCommentTarget = null;
		Editor.TextArea.Focus();
	}

	void OnCommentCancel(object? sender, RoutedEventArgs e)
	{
		CommentBox.Text = "";
		CommentPopup.IsOpen = false;
		inlineCommentTarget = null;
		Editor.TextArea.Focus();
	}

	public void ToggleBlameCommand() => ToggleBlameAsync().HandleExceptions();
	public void GoToDefinitionCommand() => NavigateToDefinitionAtCaret();
	public void FindReferencesCommand() => ShowReferencesAtCaret();
	public void HighlightOccurrencesCommand() => HighlightOccurrencesAtCaretAsync().HandleExceptions();

	void OnCtxGoToDefinition(object? s, RoutedEventArgs e) => GoToDefinitionCommand();
	void OnCtxFindReferences(object? s, RoutedEventArgs e) => FindReferencesCommand();
	/// <summary>Places the caret and highlights occurrences there, for driving checks.</summary>
	public void HighlightAtCommand(int line, int column)
	{
		if (line > 0)
			Editor.TextArea.Caret.Line = line;
		Editor.TextArea.Caret.Column = column;
		HighlightOccurrencesAtCaretAsync().HandleExceptions();
	}

	void OnCtxHighlightOccurrences(object? s, RoutedEventArgs e) => HighlightOccurrencesCommand();
	void OnCtxNextHunk(object? s, RoutedEventArgs e) => JumpToHunk(1);
	void OnCtxPrevHunk(object? s, RoutedEventArgs e) => JumpToHunk(-1);
	void OnCtxToggleBlame(object? s, RoutedEventArgs e) => ToggleBlameCommand();
	void OnCtxComment(object? s, RoutedEventArgs e) => CommentAtCaretCommand();

	void OnCtxNextUncovered(object? s, RoutedEventArgs e) => JumpToNextUncovered();

	void OnCtxNextCommit(object? s, RoutedEventArgs e)
		=> App.Workspace?.StepCommitScopeAsync(1).HandleExceptions();

	void OnCtxPrevCommit(object? s, RoutedEventArgs e)
		=> App.Workspace?.StepCommitScopeAsync(-1).HandleExceptions();

	void OnCtxHistoryOfSelection(object? s, RoutedEventArgs e)
	{
		string text = Editor.SelectedText;
		if (viewModel is null || string.IsNullOrWhiteSpace(text))
			return;
		App.Workspace?.RequestPickaxe(text, viewModel.File.Path);
	}
	void OnCtxCopy(object? s, RoutedEventArgs e) => Editor.Copy();

	void OnCtxCallGraph(object? s, RoutedEventArgs e) => ShowCallGraphCommand();

	public void ShowCallGraphCommand()
	{
		if (CaretBlobPosition() is { } pos)
		{
			App.Workspace?.Factory?.ShowPane("CallGraph");
			App.Workspace?.RequestCallGraphAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide).HandleExceptions();
		}
	}

	/// <summary>Opens VS Code on the worktree of the caret's side (base for removed lines,
	/// head otherwise) at the caret position, for stepping through the reviewed revision
	/// with a real debugger.</summary>
	void OnCtxDebugInVsCode(object? s, RoutedEventArgs e)
	{
		if (CaretBlobPosition() is not { } pos)
			return;
		App.Workspace?.OpenInVsCodeAsync(pos.OldSide, pos.RelPath, pos.Line).HandleExceptions();
	}

	void OnPointerPressedForContextMenu(object? sender, PointerPressedEventArgs e)
	{
		// Right-click moves the caret to the click point first, so the context-menu
		// commands act on the symbol that was clicked, matching IDE behavior.
		if (!e.GetCurrentPoint(Editor).Properties.IsRightButtonPressed)
			return;
		var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
		if (position is null)
			return;
		Editor.TextArea.Caret.Line = position.Value.Line;
		Editor.TextArea.Caret.Column = position.Value.Column;
	}

	#endregion

	void OnSemanticsChanged() => Dispatcher.UIThread.Post(QueueSemanticsRefresh);

	void OnCoverageChanged()
	{
		Dispatcher.UIThread.Post(() => {
			var hits = viewModel is not null
				? App.Workspace?.Coverage?.GetValueOrDefault(viewModel.File.Path)
				: null;
			coverageMargin.Tags = model?.Tags;
			coverageMargin.HitsByNewLine = hits;
			bool wanted = hits is not null && viewModel is not { Historical: true };
			if (wanted && !coverageMarginVisible)
				Editor.TextArea.LeftMargins.Insert(0, coverageMargin);
			else if (!wanted && coverageMarginVisible)
				Editor.TextArea.LeftMargins.Remove(coverageMargin);
			coverageMarginVisible = wanted;
			coverageMargin.InvalidateVisual();
		});
	}

	void OnThemeChangedForSemantics(object? sender, EventArgs e) => QueueSemanticsRefresh();

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (viewModel is not null)
			viewModel.CaretRequested -= OnCaretRequested;
		if (DataContext is not DiffDocumentViewModel vm)
			return;
		viewModel = vm;
		viewsByDocument.AddOrUpdate(vm, this);
		vm.CaretRequested += OnCaretRequested;
		model = vm.Model;
		Editor.SyntaxHighlighting = HighlightingService.GetByExtension(Path.GetExtension(vm.File.Path));
		Editor.Text = vm.Model.Text;
		margin.Tags = vm.Model.Tags;
		margin.InvalidateMeasure();
		Overview.Attach(Editor, vm.Model.Tags);
		InstallFoldsAndGaps(vm.Model);
		ApplyMarginCursors();
		referenceGenerator.References = null;
		markers.RemoveAll(_ => true);
		QueueSemanticsRefresh();
		OnCommentsChangedForThreads();
		if (vm.TakePendingCaretLine() is int line)
			Dispatcher.UIThread.Post(() => MoveCaretToLine(line));
		if (ActiveView == this)
			ActiveViewChanged?.Invoke();
	}

	#region Semantic layer (colors + clickable spans)

	void QueueSemanticsRefresh()
	{
		if (semanticsRefreshQueued)
			return;
		semanticsRefreshQueued = true;
		Dispatcher.UIThread.Post(() => {
			semanticsRefreshQueued = false;
			RefreshSemanticsAsync().HandleExceptions();
		}, DispatcherPriority.Background);
	}

	async Task RefreshSemanticsAsync()
	{
		if (model is null || viewModel is null || viewModel.Historical || App.Workspace is not { } ws)
			return;
		var m = model;
		var vm = viewModel;

		var headSem = ws.SemanticsFor(oldSide: false);
		var baseSem = ws.SemanticsFor(oldSide: true);
		var headTokens = await TokensForSideAsync(headSem, vm.File.Path, m, oldSide: false);
		bool hasRemoved = m.Tags.Any(t => t.Kind == DiffLineKind.Removed);
		var baseTokens = hasRemoved
			? await TokensForSideAsync(baseSem, vm.File.OldPath, m, oldSide: true)
			: [];
		if (model != m || viewModel != vm)
			return; // document changed while we were computing

		var rich = new RichTextModel();
		var segments = new TextSegmentCollection<ReferenceSegment>();
		AddTokens(rich, segments, headTokens, oldSide: false);
		AddTokens(rich, segments, baseTokens, oldSide: true);

		if (semanticColorizer is not null)
			Editor.TextArea.TextView.LineTransformers.Remove(semanticColorizer);
		semanticColorizer = new RichTextColorizer(rich);
		Editor.TextArea.TextView.LineTransformers.Add(semanticColorizer);
		referenceGenerator.References = segments;
		Editor.TextArea.TextView.Redraw();
	}

	/// <summary>
	/// Tokens for one side of the diff. Token positions are offsets into the text they
	/// were computed from, so the loaded workspace's are only usable when it holds the
	/// revision on screen - which it does not when a single commit of a file is being
	/// read and later commits change it. The displayed text is then classified on its
	/// own: less knowledgeable, but aligned with what is actually shown.
	/// </summary>
	static async Task<IReadOnlyList<SemanticToken>> TokensForSideAsync(
		RoslynWorkspaceService? semantics, string relPath, DiffDocumentModel model, bool oldSide)
	{
		var (displayed, _) = model.GetSideText(oldSide);
		if (displayed.Length == 0)
			return [];
		if (semantics is { State: SemanticState.Ready or SemanticState.SyntaxOnly }
			&& await semantics.GetDocumentTextAsync(relPath, CancellationToken.None) is { } loaded
			&& Same(loaded, displayed))
		{
			return await semantics.GetSemanticTokensAsync(relPath, CancellationToken.None);
		}
		return semantics is { State: SemanticState.Ready or SemanticState.SyntaxOnly }
			? await semantics.GetSemanticTokensForTextAsync(relPath, displayed, CancellationToken.None)
			: [];

		static bool Same(string a, string b) => string.Equals(
			a.ReplaceLineEndings("\n").TrimEnd('\n'),
			b.ReplaceLineEndings("\n").TrimEnd('\n'),
			StringComparison.Ordinal);
	}

	void AddTokens(RichTextModel rich, TextSegmentCollection<ReferenceSegment> segments,
		IReadOnlyList<SemanticToken> tokens, bool oldSide)
	{
		if (model is null)
			return;
		foreach (var token in tokens)
		{
			int? docLine = oldSide ? model.DocLineFromOldLine(token.Line) : model.DocLineFromNewLine(token.Line);
			if (docLine is null || docLine > Editor.Document.LineCount)
				continue;
			var tag = model.Tags[docLine.Value - 1];
			// Context lines exist on both sides; color them once, from the head tokens.
			if (oldSide && tag.Kind != DiffLineKind.Removed)
				continue;
			var line = Editor.Document.GetLineByNumber(docLine.Value);
			int offset = line.Offset + token.Column - 1;
			if (token.Column - 1 + token.Length > line.Length)
				continue;
			if (ClassificationColors.Get(token.Classification) is { } color)
				rich.SetHighlighting(offset, token.Length, color);
			segments.Add(new ReferenceSegment {
				StartOffset = offset,
				Length = token.Length,
				Kind = ReferenceMode.Link,
				Reference = new TokenRef(oldSide, token.Line, token.Column),
			});
		}
	}

	#endregion

	void OnCaretRequested(int docLine)
	{
		Dispatcher.UIThread.Post(() => MoveCaretToLine(docLine));
	}

	void MoveCaretToLine(int line)
	{
		if (line < 1 || line > Editor.Document.LineCount)
			return;
		int offset = Editor.Document.GetLineByNumber(line).Offset;
		if (foldingManager is not null)
		{
			foreach (var folding in foldingManager.GetFoldingsContaining(offset))
				folding.IsFolded = false;
		}
		// A line hidden as context has to be given back before the caret can sit on it.
		contextGaps?.Reveal(line);
		Editor.TextArea.Caret.Line = line;
		Editor.TextArea.Caret.Column = 1;
		Editor.ScrollToLine(line);
		Editor.TextArea.Focus();
	}

	/// <summary>
	/// Folds are the code's structure only - types, members, #regions. Unchanged context is
	/// hidden by <see cref="contextGaps"/> instead, which is why expanding a method no longer
	/// reveals context and collapsing everything no longer swallows the change.
	/// </summary>
	void InstallFoldsAndGaps(DiffDocumentModel m)
	{
		foldingManager ??= FoldingManager.Install(Editor.TextArea);
		structuralRanges = [];
		if (viewModel is { } vm && vm.File.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
		{
			bool oldSide = vm.File.Kind == Core.Diff.FileChangeKind.Deleted;
			var (sideText, sideToDocLine) = m.GetSideText(oldSide);
			structuralRanges.AddRange(DiffFolding.Members(sideText, sideToDocLine));
		}
		// The fold ranges start where each member's declaration does, which is exactly what
		// the gaps need to know to keep a signature above a hunk in view.
		contextGaps?.Install(m.Tags, m.Hunks.Count > 0, [.. structuralRanges.Select(r => r.StartLine)]);
		RefreshFoldings();
	}

	/// <summary>
	/// Installs the structural folds that apply to what is actually shown. A fold beginning
	/// inside hidden context is left out: the gap's control stands for all those lines at
	/// once, so the margin would draw that fold's marker beside the control and offer to
	/// collapse code the reader cannot see. They come back as the context does.
	/// </summary>
	void RefreshFoldings()
	{
		if (foldingManager is null)
			return;
		var shown = structuralRanges.Where(r => contextGaps?.Hides(r.StartLine) != true).ToList();
		foldingManager.Clear();
		foldingManager.UpdateFoldings(FoldInstaller.ToFoldings(Editor.Document, shown), -1);
	}

	void OnEditorKeyDown(object? sender, KeyEventArgs e)
	{
		// The search panel is a child of the text area, so what is typed into its box tunnels
		// through here on the way down. A review gesture is a letter to anyone typing one:
		// leave every keystroke aimed at a text box alone.
		if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
			return;
		switch (e.Key, e.KeyModifiers)
		{
			// n/p are the review's own keys; Ctrl+Down/Up are the ones a hand arrives with.
			// They cost AvaloniaEdit's scroll-by-line, which a diff nobody types into has
			// little use for - and this handler tunnels, so the editor never sees them.
			case (Key.N, KeyModifiers.None):
			case (Key.Down, KeyModifiers.Control):
				JumpToHunk(1);
				e.Handled = true;
				break;
			case (Key.P, KeyModifiers.None):
			case (Key.Up, KeyModifiers.Control):
				JumpToHunk(-1);
				e.Handled = true;
				break;
			case (Key.OemCloseBrackets, KeyModifiers.Control):
				App.Workspace?.StepCommitScopeAsync(1).HandleExceptions();
				e.Handled = true;
				break;
			case (Key.OemOpenBrackets, KeyModifiers.Control):
				App.Workspace?.StepCommitScopeAsync(-1).HandleExceptions();
				e.Handled = true;
				break;
			case (Key.OemCloseBrackets, KeyModifiers.None):
				App.Workspace?.OpenAdjacentFileAsync(1).HandleExceptions();
				e.Handled = true;
				break;
			case (Key.OemOpenBrackets, KeyModifiers.None):
				App.Workspace?.OpenAdjacentFileAsync(-1).HandleExceptions();
				e.Handled = true;
				break;
			case (Key.V, KeyModifiers.None):
				App.Workspace?.ToggleViewedAndAdvanceAsync().HandleExceptions();
				e.Handled = true;
				break;
			case (Key.O, KeyModifiers.None):
				App.Workspace?.ToggleOverviewAsync().HandleExceptions();
				e.Handled = true;
				break;
			case (Key.F12, KeyModifiers.None):
				NavigateToDefinitionAtCaret();
				e.Handled = true;
				break;
			case (Key.F12, KeyModifiers.Shift):
				ShowReferencesAtCaret();
				e.Handled = true;
				break;
			case (Key.U, KeyModifiers.None):
				JumpToNextUncovered();
				e.Handled = true;
				break;
			case (Key.B, KeyModifiers.None):
				ToggleBlameAsync().HandleExceptions();
				e.Handled = true;
				break;
			case (Key.C, KeyModifiers.None):
				CommentAtCaretCommand();
				e.Handled = true;
				break;
			case (Key.Escape, KeyModifiers.None):
				markers.RemoveAll(_ => true);
				break;
			case (Key.Left, KeyModifiers.Alt):
				App.Workspace?.GoBackAsync().HandleExceptions();
				e.Handled = true;
				break;
			case (Key.Right, KeyModifiers.Alt):
				App.Workspace?.GoForwardAsync().HandleExceptions();
				e.Handled = true;
				break;
		}
	}

	// Position of the last left-button press; null while no press is in flight.
	Avalonia.Point? clickStart;

	// WPF's default minimum drag distance; a release farther than this is a drag.
	const double MinimumDragDistance = 4;

	void OnTextAreaPointerPressedForClick(object? sender, PointerPressedEventArgs e)
	{
		clickStart = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
			? e.GetPosition(this)
			: null;
	}

	void OnTextViewPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		var start = clickStart;
		clickStart = null;
		if (e.InitialPressMouseButton != MouseButton.Left || start is null)
			return;
		var delta = e.GetPosition(this) - start.Value;
		if (Math.Abs(delta.X) >= MinimumDragDistance || Math.Abs(delta.Y) >= MinimumDragDistance)
			return;
		// A stationary click has already placed the caret. Ctrl+Click navigates; a plain
		// click on a symbol highlights its occurrences in this document.
		if (e.KeyModifiers == KeyModifiers.Control)
		{
			Editor.TextArea.ClearSelection();
			NavigateToDefinitionAtCaret();
		}
		else if (e.KeyModifiers == KeyModifiers.None && Editor.TextArea.Selection.IsEmpty)
		{
			HighlightOccurrencesAtCaretAsync().HandleExceptions();
		}
	}

	/// <summary>Caret as a blob position: head side on context/added lines, base side on
	/// removed lines (whose code only exists at the merge base).</summary>
	(string RelPath, int Line, int Column, bool OldSide)? CaretBlobPosition()
	{
		if (model is null || viewModel is null)
			return null;
		int docLine = Editor.TextArea.Caret.Line;
		if (docLine < 1 || docLine > model.Tags.Count)
			return null;
		var tag = model.Tags[docLine - 1];
		if (tag.NewLine > 0)
			return (viewModel.File.Path, tag.NewLine, Editor.TextArea.Caret.Column, false);
		if (tag.OldLine > 0)
			return (viewModel.File.OldPath, tag.OldLine, Editor.TextArea.Caret.Column, true);
		return null;
	}

	void NavigateToDefinitionAtCaret()
	{
		if (viewModel is null or { Historical: true } || CaretBlobPosition() is not { } pos)
			return;
		var origin = new ReviewWorkspace.NavEntryOrigin(viewModel.Id, Editor.TextArea.Caret.Line);
		App.Workspace?.NavigateToDefinitionAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide, origin).HandleExceptions();
	}

	void ShowReferencesAtCaret()
	{
		if (viewModel is { Historical: true } || CaretBlobPosition() is not { } pos)
			return;
		App.Workspace?.ShowReferencesAtAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide).HandleExceptions();
	}

	async Task HighlightOccurrencesAtCaretAsync()
	{
		markers.RemoveAll(_ => true);
		if (viewModel is { Historical: true } || CaretBlobPosition() is not { } pos || model is null || App.Workspace is not { } ws)
			return;
		var occurrences = await ws.FindOccurrencesAsync(pos.RelPath, pos.Line, pos.Column, pos.OldSide);
		foreach (var occ in occurrences)
		{
			int? docLine = pos.OldSide ? model.DocLineFromOldLine(occ.Line) : model.DocLineFromNewLine(occ.Line);
			if (docLine is null || docLine > Editor.Document.LineCount)
				continue;
			var line = Editor.Document.GetLineByNumber(docLine.Value);
			if (occ.Column - 1 + occ.Length > line.Length)
				continue;
			var marker = markers.Create(line.Offset + occ.Column - 1, occ.Length);
			marker.BackgroundColor = occ.Classification == "definition" ? DefinitionOccurrenceColor : OccurrenceColor;
		}
	}

	void JumpToNextUncovered()
	{
		if (model is null || viewModel is null || App.Workspace is not { } ws)
			return;
		int start = Editor.TextArea.Caret.Line;
		for (int line = start + 1; line <= model.Tags.Count; line++)
		{
			var tag = model.Tags[line - 1];
			if (tag.NewLine > 0 && ws.IsUncoveredAdded(viewModel.File.Path, tag.NewLine))
			{
				MoveCaretToLine(line);
				return;
			}
		}
	}

	void JumpToHunk(int direction)
	{
		if (model is null || model.Hunks.Count == 0)
			return;
		int caretLine = Editor.TextArea.Caret.Line;
		HunkSpan? target = direction > 0
			? model.Hunks.Cast<HunkSpan?>().FirstOrDefault(h => h!.Value.FirstDocLine > caretLine)
			: model.Hunks.Cast<HunkSpan?>().LastOrDefault(h => h!.Value.FirstDocLine < caretLine);
		if (target is null)
			return;
		MoveCaretToLine(target.Value.FirstDocLine);
	}

	#region Blame

	readonly BlameMargin blameMargin = new();
	bool blameVisible;

	async Task ToggleBlameAsync()
	{
		if (blameVisible)
		{
			Editor.TextArea.LeftMargins.Remove(blameMargin);
			blameVisible = false;
			return;
		}
		if (model is null || viewModel is null || App.Workspace is not { } ws || ws.HeadSha is null)
			return;
		var m = model;
		var vm = viewModel;
		IReadOnlyList<Core.Git.BlameLine> newBlame = [];
		IReadOnlyList<Core.Git.BlameLine> oldBlame = [];
		string newRev = vm.Historical ? vm.HistoricalSha! : ws.HeadSha;
		string? oldRev = vm.Historical ? vm.HistoricalSha + "^" : ws.BaseSha;
		try
		{
			if (vm.File.Kind != FileChangeKind.Deleted)
				newBlame = await ws.Git.BlameAsync(newRev, vm.File.Path);
			if (oldRev is not null && m.Tags.Any(t => t.Kind == DiffLineKind.Removed))
				oldBlame = await ws.Git.BlameAsync(oldRev, vm.File.OldPath);
		}
		catch (Core.Infra.ToolFailedException)
		{
			return; // e.g. blaming a base-only view at head; blame is best-effort
		}
		if (model != m)
			return;
		var newByLine = newBlame.ToDictionary(b => b.FinalLine);
		var oldByLine = oldBlame.ToDictionary(b => b.FinalLine);
		var perDoc = new Core.Git.BlameLine?[m.Tags.Count];
		for (int i = 0; i < m.Tags.Count; i++)
		{
			var tag = m.Tags[i];
			perDoc[i] = tag.NewLine > 0
				? newByLine.GetValueOrDefault(tag.NewLine)
				: oldByLine.GetValueOrDefault(tag.OldLine);
		}
		blameMargin.SetLines(perDoc);
		Editor.TextArea.LeftMargins.Insert(0, blameMargin);
		blameVisible = true;
	}

	#endregion

	#region Hover tooltip

	void OnPointerMovedForHover(object? sender, PointerEventArgs e)
	{
		lastPointerPosition = e.GetPosition(Editor);
		UpdateTextCursor(e);
		ToolTip.SetIsOpen(Editor, false);
		hoverTimer.Stop();
		hoverTimer.Start();
	}

	bool foldCursorActive;

	/// <summary>Hand cursor over collapsed-fold markers - they are drawn text, not
	/// controls, so the affordance has to be set at the view level.</summary>
	void UpdateTextCursor(PointerEventArgs e)
	{
		var view = Editor.TextArea.TextView;
		bool overFold = false;
		var point = e.GetPosition(view) + view.ScrollOffset;
		var visualLine = view.GetVisualLineFromVisualTop(point.Y);
		if (visualLine is not null)
		{
			var textLine = visualLine.GetTextLineByVisualYPosition(point.Y);
			int column = visualLine.GetVisualColumn(textLine, point.X, allowVirtualSpace: false);
			var element = visualLine.Elements.FirstOrDefault(el =>
				el.VisualColumn <= column && column < el.VisualColumn + el.VisualLength);
			overFold = element?.GetType().Name.Contains("Folding", StringComparison.Ordinal) == true;
		}
		if (overFold != foldCursorActive)
		{
			foldCursorActive = overFold;
			view.Cursor = new Cursor(overFold ? StandardCursorType.Hand : StandardCursorType.Ibeam);
		}
	}

	void CancelHover()
	{
		hoverTimer.Stop();
		ToolTip.SetIsOpen(Editor, false);
	}

	void OnHoverTimerTick(object? sender, EventArgs e)
	{
		hoverTimer.Stop();
		ShowHoverAsync().HandleExceptions();
	}

	async Task ShowHoverAsync()
	{
		if (model is null || viewModel is null || viewModel.Historical || App.Workspace is not { } ws)
			return;
		var position = Editor.GetPositionFromPoint(lastPointerPosition);
		if (position is null)
			return;
		int docLine = position.Value.Line;
		if (docLine < 1 || docLine > model.Tags.Count)
			return;
		var tag = model.Tags[docLine - 1];
		bool oldSide = tag.NewLine == 0;
		if (oldSide && tag.OldLine == 0)
			return;
		string relPath = oldSide ? viewModel.File.OldPath : viewModel.File.Path;
		int blobLine = oldSide ? tag.OldLine : tag.NewLine;
		var sem = ws.SemanticsFor(oldSide);
		if (sem is not { State: SemanticState.Ready or SemanticState.SyntaxOnly })
			return;
		int? pos = await sem.GetPositionAsync(relPath, blobLine, position.Value.Column, CancellationToken.None);
		if (pos is null)
			return;
		string? text = await sem.GetQuickInfoAsync(relPath, pos.Value, CancellationToken.None);
		if (string.IsNullOrEmpty(text))
			return;
		ToolTip.SetTip(Editor, text);
		ToolTip.SetIsOpen(Editor, true);
	}

	#endregion
}
