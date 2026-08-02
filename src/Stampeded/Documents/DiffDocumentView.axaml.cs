using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
	FoldingManager? foldingManager;
	DiffDocumentModel? model;

	public DiffDocumentView()
	{
		InitializeComponent();
		SearchPanel.Install(Editor);
		HighlightingService.EnsureRegistered();
		Editor.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => model?.Tags));
		Editor.TextArea.LeftMargins.Insert(0, margin);
		Editor.TextArea.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (DataContext is not DiffDocumentViewModel vm)
			return;
		model = vm.Model;
		Editor.SyntaxHighlighting = HighlightingService.GetByExtension(Path.GetExtension(vm.File.Path));
		Editor.Text = vm.Model.Text;
		margin.Tags = vm.Model.Tags;
		margin.InvalidateMeasure();
		Overview.Attach(Editor, vm.Model.Tags);
		InstallFoldings(vm.Model);
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
		if (e.KeyModifiers != KeyModifiers.None)
			return;
		switch (e.Key)
		{
			case Key.N:
				JumpToHunk(1);
				e.Handled = true;
				break;
			case Key.P:
				JumpToHunk(-1);
				e.Handled = true;
				break;
			case Key.OemCloseBrackets:
				App.Workspace?.OpenAdjacentFileAsync(1).HandleExceptions();
				e.Handled = true;
				break;
			case Key.OemOpenBrackets:
				App.Workspace?.OpenAdjacentFileAsync(-1).HandleExceptions();
				e.Handled = true;
				break;
			case Key.V:
				App.Workspace?.ToggleViewedAndAdvanceAsync().HandleExceptions();
				e.Handled = true;
				break;
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
		int line = target.Value.FirstDocLine;
		int offset = Editor.Document.GetLineByNumber(line).Offset;
		if (foldingManager is not null)
		{
			foreach (var folding in foldingManager.GetFoldingsContaining(offset))
				folding.IsFolded = false;
		}
		Editor.TextArea.Caret.Line = line;
		Editor.TextArea.Caret.Column = 1;
		Editor.ScrollToLine(line);
	}
}
