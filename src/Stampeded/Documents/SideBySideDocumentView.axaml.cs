using Avalonia.Controls;

using AvaloniaEdit;
using AvaloniaEdit.Search;

using Stampeded.Core.Diff;
using Stampeded.Diff;
using Stampeded.Editor;

namespace Stampeded.Documents;

/// <summary>
/// Two synchronized editors fed from one alignment; equal line counts (Filler rows on
/// the shorter side) make scroll sync a plain offset copy with a re-entrancy guard.
/// </summary>
public partial class SideBySideDocumentView : UserControl
{
	readonly DiffLineNumberMargin leftMargin = new();
	readonly DiffLineNumberMargin rightMargin = new();
	IReadOnlyList<DiffLineTag>? leftTags;
	IReadOnlyList<DiffLineTag>? rightTags;
	bool syncing;

	public SideBySideDocumentView()
	{
		InitializeComponent();
		SearchPanel.Install(Left);
		SearchPanel.Install(Right);
		Left.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => leftTags));
		Right.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => rightTags));
		Left.TextArea.LeftMargins.Insert(0, leftMargin);
		Right.TextArea.LeftMargins.Insert(0, rightMargin);
		Left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(Left, Right);
		Right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(Right, Left);
	}

	void Sync(TextEditor source, TextEditor target)
	{
		if (syncing)
			return;
		syncing = true;
		try
		{
			var offset = source.TextArea.TextView.ScrollOffset;
			target.ScrollToVerticalOffset(offset.Y);
			target.ScrollToHorizontalOffset(offset.X);
		}
		finally
		{
			syncing = false;
		}
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (DataContext is not SideBySideDocumentViewModel vm)
			return;
		var highlighting = HighlightingService.GetByExtension(Path.GetExtension(vm.File.Path));
		Left.SyntaxHighlighting = highlighting;
		Right.SyntaxHighlighting = highlighting;
		Left.Text = vm.Pair.LeftText;
		Right.Text = vm.Pair.RightText;
		leftTags = vm.Pair.LeftTags;
		rightTags = vm.Pair.RightTags;
		leftMargin.Tags = vm.Pair.LeftTags;
		rightMargin.Tags = vm.Pair.RightTags;
		leftMargin.InvalidateMeasure();
		rightMargin.InvalidateMeasure();
	}
}
