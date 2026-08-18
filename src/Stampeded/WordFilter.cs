namespace Stampeded;

/// <summary>
/// What a filter box means by a match: every whitespace-separated word has to appear somewhere
/// in the row, so a second word narrows rather than widens. Case is ignored - what is typed in
/// a hurry is lower case, and what it matches is a path or a title.
/// </summary>
static class WordFilter
{
	public static bool Matches(string filter, params string?[] fields)
	{
		var words = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		return words.All(word => fields.Any(f => f is not null
			&& f.Contains(word, StringComparison.OrdinalIgnoreCase)));
	}
}
