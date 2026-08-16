using System.Globalization;

namespace Stampeded;

/// <summary>
/// How far the window's content is scaled, kept between sessions. It is set for the screen and
/// the eyes in front of it, neither of which changes between one review and the next, so having
/// to set it again every start is having to set it for nothing.
/// </summary>
public static class ZoomPreference
{
	const string FileName = "zoom.txt";

	public static double Load(double min, double max)
		=> double.TryParse(UserData.Read(FileName), NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom)
			// A file written by an older build, or by hand, does not get to make the window
			// unreadable.
			? Math.Clamp(zoom, min, max)
			: 1.0;

	public static void Save(double zoom)
		=> UserData.Write(FileName, zoom.ToString("0.###", CultureInfo.InvariantCulture));
}
