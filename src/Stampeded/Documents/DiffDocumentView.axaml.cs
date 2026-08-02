using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using AvaloniaEdit.Folding;
using AvaloniaEdit.Search;

using Stampeded.Core.Diff;
using Stampeded.Diff;
using Stampeded.Editor;

namespace Stampeded.Documents;

public partial class DiffDocumentView : UserControl
{
	// Unchanged context kept visible around each hunk when folding the rest.
	const int FoldContext = 3;

	readonly DiffLineNumberMargin margin = new();
	readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
	Avalonia.Point lastPointerPosition;
	FoldingManager? foldingManager;
	DiffDocumentModel? model;
	DiffDocumentViewModel? viewModel;

	public DiffDocumentView()
	{
		InitializeComponent();
		SearchPanel.Install(Editor);
		HighlightingService.EnsureRegistered();
		Editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => model?.Tags));
		Editor.TextArea.LeftMargins.Insert(0, margin);
		Editor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
		Editor.TextArea.TextView.AddHandler(PointerReleasedEvent, OnTextViewPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
		Editor.TextArea.TextView.PointerMoved += OnPointerMovedForHover;
		Editor.TextArea.TextView.PointerExited += (_, _) => CancelHover();
		hoverTimer.Tick += OnHoverTimerTick;
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (viewModel is not null)
			viewModel.CaretRequested -= OnCaretRequested;
		if (DataContext is not DiffDocumentViewModel vm)
			return;
		viewModel = vm;
		vm.CaretRequested += OnCaretRequested;
		model = vm.Model;
		Editor.SyntaxHighlighting = HighlightingService.GetByExtension(Path.GetExtension(vm.File.Path));
		Editor.Text = vm.Model.Text;
		margin.Tags = vm.Model.Tags;
		margin.InvalidateMeasure();
		Overview.Attach(Editor, vm.Model.Tags);
		InstallFoldings(vm.Model);
		if (vm.TakePendingCaretLine() is int line)
			Dispatcher.UIThread.Post(() => MoveCaretToLine(line));
	}

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
		Editor.TextArea.Caret.Line = line;
		Editor.TextArea.Caret.Column = 1;
		Editor.ScrollToLine(line);
		Editor.TextArea.Focus();
	}

	void InstallFoldings(DiffDocumentModel m)
	{
		foldingManager ??= FoldingManager.Install(Editor.TextArea);
		var foldings = new List<NewFolding>();
		int runStart = -1; // 0-based tag index of the current context run
		for (int i = 0; i <= m.Tags.Count; i++)
		{
			bool context = i < m.Tags.Count && m.Tags[i].Kind == DiffLineKind.Context;
			if (context && runStart < 0)
				runStart = i;
			else if (!context && runStart >= 0)
			{
				AddFolding(m, foldings, runStart, i - 1);
				runStart = -1;
			}
		}
		foldingManager.Clear();
		foldingManager.UpdateFoldings(foldings.OrderBy(f => f.StartOffset).ToList(), -1);
	}

	void AddFolding(DiffDocumentModel m, List<NewFolding> foldings, int firstTag, int lastTag)
	{
		// A source view (identity model) has no hunks; keep it entirely unfolded.
		if (m.Hunks.Count == 0)
			return;
		// Keep FoldContext lines visible on each side; at the document edges the whole
		// run may fold except the context adjoining the hunk.
		int foldFirst = firstTag == 0 ? firstTag : firstTag + FoldContext;
		int foldLast = lastTag == m.Tags.Count - 1 ? lastTag : lastTag - FoldContext;
		int hidden = foldLast - foldFirst + 1;
		if (hidden < 2)
			return;
		var startLine = Editor.Document.GetLineByNumber(foldFirst + 1);
		var endLine = Editor.Document.GetLineByNumber(foldLast + 1);
		foldings.Add(new NewFolding(startLine.Offset, endLine.EndOffset) {
			Name = $"... {hidden} unchanged lines",
			DefaultClosed = true,
		});
	}

	void OnEditorKeyDown(object? sender, KeyEventArgs e)
	{
		switch (e.Key, e.KeyModifiers)
		{
			case (Key.N, KeyModifiers.None):
				JumpToHunk(1);
				e.Handled = true;
				break;
			case (Key.P, KeyModifiers.None):
				JumpToHunk(-1);
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
			case (Key.F12, KeyModifiers.None):
				NavigateToDefinitionAtCaret();
				e.Handled = true;
				break;
			case (Key.F12, KeyModifiers.Shift):
				ShowReferencesAtCaret();
				e.Handled = true;
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

	void OnTextViewPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		// The click has already placed the caret; Ctrl+Click navigates from it.
		if (e.KeyModifiers == KeyModifiers.Control && e.InitialPressMouseButton == MouseButton.Left)
			NavigateToDefinitionAtCaret();
	}

	/// <summary>Maps the caret to a (relPath, newLine, column), null on removed/absent lines.</summary>
	(string RelPath, int NewLine, int Column)? CaretNewFilePosition()
	{
		if (model is null || viewModel is null)
			return null;
		int docLine = Editor.TextArea.Caret.Line;
		if (docLine < 1 || docLine > model.Tags.Count)
			return null;
		var tag = model.Tags[docLine - 1];
		if (tag.NewLine == 0)
			return null; // removed line: that code no longer exists at head
		return (viewModel.File.Path, tag.NewLine, Editor.TextArea.Caret.Column);
	}

	void NavigateToDefinitionAtCaret()
	{
		if (CaretNewFilePosition() is not { } pos || viewModel is null)
			return;
		var origin = new ReviewWorkspace.NavEntryOrigin(viewModel.Id, Editor.TextArea.Caret.Line);
		App.Workspace?.NavigateToDefinitionAsync(pos.RelPath, pos.NewLine, pos.Column, origin).HandleExceptions();
	}

	void ShowReferencesAtCaret()
	{
		if (CaretNewFilePosition() is not { } pos)
			return;
		App.Workspace?.ShowReferencesAtAsync(pos.RelPath, pos.NewLine, pos.Column).HandleExceptions();
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

	#region Hover tooltip

	void OnPointerMovedForHover(object? sender, PointerEventArgs e)
	{
		lastPointerPosition = e.GetPosition(Editor);
		ToolTip.SetIsOpen(Editor, false);
		hoverTimer.Stop();
		hoverTimer.Start();
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
		if (model is null || viewModel is null || App.Workspace?.Semantics is not { } sem)
			return;
		var position = Editor.GetPositionFromPoint(lastPointerPosition);
		if (position is null)
			return;
		int docLine = position.Value.Line;
		if (docLine < 1 || docLine > model.Tags.Count)
			return;
		var tag = model.Tags[docLine - 1];
		if (tag.NewLine == 0)
			return;
		int? pos = await sem.GetPositionAsync(viewModel.File.Path, tag.NewLine, position.Value.Column, CancellationToken.None);
		if (pos is null)
			return;
		string? text = await sem.GetHoverTextAsync(viewModel.File.Path, pos.Value, CancellationToken.None);
		if (string.IsNullOrEmpty(text))
			return;
		ToolTip.SetTip(Editor, text);
		ToolTip.SetIsOpen(Editor, true);
	}

	#endregion
}
