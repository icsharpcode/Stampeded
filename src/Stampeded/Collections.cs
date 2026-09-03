using System.Collections.ObjectModel;

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
