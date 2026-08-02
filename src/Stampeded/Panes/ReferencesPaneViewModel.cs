using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Dock.Model.Mvvm.Controls;

using Stampeded.Core.Roslyn;

namespace Stampeded.Panes;

public sealed partial class ReferencesState : ObservableObject
{
	[ObservableProperty]
	string status = "Shift+F12 on a symbol lists its references here.";
}

public sealed record ReferenceRow(ReferenceItem Item)
{
	// '*' marks hits on lines this PR adds/changes - the reviewer's primary question.
	public string Display => $"{(Item.InChangedLine ? "*" : " ")} {Item.RelPath}:{Item.Line}  {Item.Preview}";
}

/// <summary>
/// Find-references results; also surfaces the semantic workspace state so it is visible
/// when navigation becomes available.
/// </summary>
public class ReferencesPaneViewModel : Tool
{
	readonly ReviewWorkspace workspace;

	public ObservableCollection<ReferenceRow> Items { get; } = [];
	public ReferencesState State { get; } = new();

	public ReferencesPaneViewModel(ReviewWorkspace workspace)
	{
		this.workspace = workspace;
		workspace.ReferencesAvailable += OnReferences;
		workspace.SemanticsChanged += OnSemanticsChanged;
	}

	void OnSemanticsChanged()
	{
		var sem = workspace.Semantics;
		if (sem is null)
			return;
		string detail = sem.StateDetail.Length > 0 ? $" ({sem.StateDetail})" : "";
		string head = sem.State switch {
			SemanticState.Restoring => $"Restoring packages{detail}...",
			SemanticState.Loading => $"Loading solution{detail}...",
			SemanticState.Ready => $"Semantics ready{detail} - F12 / Shift+F12 / Ctrl+Click enabled.",
			SemanticState.SyntaxOnly => $"Syntax-only semantics{detail} - navigation may be incomplete.",
			SemanticState.Failed => $"Semantic load failed: {sem.StateDetail}",
			_ => "",
		};
		string baseState = workspace.BaseSemantics?.State switch {
			SemanticState.Ready => " Base side ready (removed code is navigable).",
			SemanticState.Restoring or SemanticState.Loading => " Loading base side...",
			SemanticState.SyntaxOnly => " Base side syntax-only.",
			_ => "",
		};
		if (head.Length > 0)
			State.Status = head + baseState;
	}

	void OnReferences(string symbolName, IReadOnlyList<ReferenceItem> items)
	{
		Items.Clear();
		foreach (var item in items)
			Items.Add(new ReferenceRow(item));
		State.Status = $"{items.Count} reference(s) to '{symbolName}'; * = on a changed line.";
	}

	public void Open(ReferenceRow row)
	{
		workspace.NavigateToFileLineAsync(row.Item.RelPath, row.Item.Line, row.Item.OldSide, record: true).HandleExceptions();
	}
}
