using Avalonia.Media;

namespace Stampeded;

/// <summary>
/// A colour per reading scope, so which one is on is answered by the look of the window rather
/// than by reading a badge. The three modes change what every file list and every diff below
/// them means - the whole change, one commit of it, or only what arrived since the last pass -
/// and mistaking one for another is the mistake that wastes a pass.
///
/// Away from red and green, which belong to the diff itself: blue is the app's own accent and
/// stays with the whole change, purple marks stepping through the commits, orange marks the
/// work since the last pass.
///
/// Two brushes that are mutated rather than replaced, like <see cref="ZoomState"/>: a style has
/// no data context to bind through, and the tab in front is painted by one. Everything that
/// wears the scope points at these two and follows them.
/// </summary>
public static class ScopePalette
{
	static readonly Color WholeChange = Color.Parse("#3794FF");
	static readonly Color CommitByCommit = Color.Parse("#A371F7");
	static readonly Color SinceLastPass = Color.Parse("#F0883E");

	/// <summary>The colour of the scope being read.</summary>
	public static SolidColorBrush Accent { get; } = new(WholeChange);

	/// <summary>The same colour as a wash behind a header that carries it. Transparent for the
	/// whole change: the default state is not a state to point at.</summary>
	public static SolidColorBrush Tint { get; } = new(WholeChange, 0);

	public static void Set(ReviewWorkspace? workspace)
	{
		(Color color, double tint) = workspace switch {
			{ Scopes.Commit: not null } => (CommitByCommit, 0.10),
			{ Scopes.InSinceLastPass: true } => (SinceLastPass, 0.10),
			// A tenth of the accent is enough to read as a coloured area and not enough to
			// fight the text on it; the whole change washes nothing at all.
			_ => (WholeChange, 0.0),
		};
		Accent.Color = color;
		Tint.Color = color;
		Tint.Opacity = tint;
	}
}
