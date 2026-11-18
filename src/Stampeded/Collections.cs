using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Stampeded;

public static class Collections
{
	/// <summary>
	/// Replaces everything in a bound list in one step.
	///
	/// The reason this exists rather than Clear() at the top of a load: a load that clears, then
	/// awaits, then adds is two loads away from showing its contents twice. The events these
	/// lists are refilled from fire more than once for one review - a pull request is read, and
	/// then what only GitHub knows arrives and says so again - so two runs overlap, both clear
	/// while the other has added nothing, and both then add the whole list. Gathering first and
	/// swapping here leaves no window to interleave in: whichever run finishes last decides, and
	/// what it decides is a complete list rather than a doubled one.
	/// </summary>
	public static void Replace<T>(this ObservableCollection<T> list, IEnumerable<T> items)
	{
		list.Clear();
		foreach (var item in items)
			list.Add(item);
	}
}

/// <summary>An observable collection that can replace its contents with one Reset event,
/// rather than notifying bindings separately for every inserted item.</summary>
public sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
	public void ReplaceAll(IEnumerable<T> items)
	{
		var replacement = items.ToList();
		Items.Clear();
		foreach (var item in replacement)
			Items.Add(item);
		OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
		OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
		OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
	}
}
