namespace Stampeded.Core.Review;

/// <summary>
/// Re-review is a different process, not a repeat: prior conclusions stay valid except
/// where the new push touched them.
/// </summary>
public static class ReReview
{
	/// <summary>Files whose viewed flag survives a head move: viewed at the previous head
	/// and untouched by the interdiff.</summary>
	public static IReadOnlyList<string> CarryOverViewed(
		IReadOnlyDictionary<string, bool> previousViewed, IReadOnlySet<string> touchedSinceLastPass)
		=> previousViewed
			.Where(kv => kv.Value && !touchedSinceLastPass.Contains(kv.Key))
			.Select(kv => kv.Key)
			.ToList();
}
