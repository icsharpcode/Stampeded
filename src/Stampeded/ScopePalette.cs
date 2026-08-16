using Avalonia.Media;

namespace Stampeded;

/// <summary>
/// A colour per reading scope, so which one is on is answered by the look of the pane rather
/// than by reading its badge. The three modes change what every file list and every diff below
/// them means - the whole change, one commit of it, or only what arrived since the last pass -
/// and mistaking one for another is the mistake that wastes a pass.
///
/// Away from red and green, which belong to the diff itself: blue is the app's own accent and
/// stays with the whole change, purple marks stepping through the commits, orange marks the
/// work since the last pass.
/// </summary>
static class ScopePalette
{
	public static readonly IBrush WholeChange = Brush("#3794FF");
	public static readonly IBrush CommitByCommit = Brush("#A371F7");
	public static readonly IBrush SinceLastPass = Brush("#F0883E");

	/// <summary>The colour of the scope a review is being read in.</summary>
	public static IBrush Accent(ReviewWorkspace? workspace) => workspace switch {
		{ CommitScope: not null } => CommitByCommit,
		{ InSinceLastPassScope: true } => SinceLastPass,
		_ => WholeChange,
	};

	/// <summary>The same colour as a wash behind the header that carries it. Transparent for
	/// the whole change: the default state is not a state to point at.</summary>
	public static IBrush Tint(ReviewWorkspace? workspace) => workspace switch {
		{ CommitScope: not null } => Wash("#A371F7"),
		{ InSinceLastPassScope: true } => Wash("#F0883E"),
		_ => Brushes.Transparent,
	};

	static IBrush Brush(string color) => new SolidColorBrush(Color.Parse(color));

	// A tenth of the accent: enough to read as a coloured area, not enough to fight the text
	// on it in either theme.
	static IBrush Wash(string color) => new SolidColorBrush(Color.Parse(color), 0.10);
}
