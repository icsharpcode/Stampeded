using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
	bool scrollWired;
	int wireAttempts;

	public SideBySideDocumentView()
	{
		InitializeComponent();
		SearchPanel.Install(Left);
		SearchPanel.Install(Right);
		Left.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => leftTags));
		Right.TextArea.TextView.BackgroundRenderers.Add(new DiffLineBackgroundRenderer(() => rightTags));
		Left.TextArea.LeftMargins.Insert(0, leftMargin);
		Right.TextArea.LeftMargins.Insert(0, rightMargin);
	}

	protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		Dispatcher.UIThread.Post(WireScrollSync, DispatcherPriority.Loaded);
	}

	// The editors' ScrollViewers only exist once their templates are applied; sync their
	// Offset properties directly - equal line counts on both sides make a plain copy exact.
	void WireScrollSync()
	{
		if (scrollWired)
			return;
		var leftScroll = Left.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
		var rightScroll = Right.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
		if (leftScroll is null || rightScroll is null)
		{
			if (wireAttempts++ < 20)
				Dispatcher.UIThread.Post(WireScrollSync, DispatcherPriority.Background);
			return;
		}
		scrollWired = true;
		leftScroll.ScrollChanged += (_, _) => Sync(leftScroll, rightScroll);
		rightScroll.ScrollChanged += (_, _) => Sync(rightScroll, leftScroll);
	}

	void Sync(ScrollViewer source, ScrollViewer target)
	{
		if (syncing || target.Offset == source.Offset)
			return;
		syncing = true;
		try
		{
			target.Offset = source.Offset;
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
