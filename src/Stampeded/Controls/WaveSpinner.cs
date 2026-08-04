using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Stampeded.Controls;

/// <summary>
/// Eight squares with a brightness/size wave sweeping across, then a beat with all of
/// them dim - the KDE Plasma loading idiom. Drawn directly; animates only while
/// effectively visible.
/// </summary>
public sealed class WaveSpinner : Control
{
	public static readonly StyledProperty<double> SquareSizeProperty =
		AvaloniaProperty.Register<WaveSpinner, double>(nameof(SquareSize), 8);

	public double SquareSize {
		get => GetValue(SquareSizeProperty);
		set => SetValue(SquareSizeProperty, value);
	}

	const int Count = 8;
	// The wave sweeps this many positions each direction and bounces at the ends; the
	// overshoot past the row lets the crest fade out before it turns around.
	const double Travel = Count + 3;

	static readonly Color BaseColor = Color.Parse("#3794FF");

	readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
	double wavePhase;

	public WaveSpinner()
	{
		timer.Tick += (_, _) => {
			wavePhase = (wavePhase + 0.35) % (2 * Travel);
			InvalidateVisual();
		};
	}

	protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
	{
		base.OnAttachedToVisualTree(e);
		timer.Start();
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		timer.Stop();
		base.OnDetachedFromVisualTree(e);
	}

	protected override Size MeasureOverride(Size availableSize)
	{
		double size = SquareSize;
		return new Size(Count * size * 2 - size, size * 1.8);
	}

	public override void Render(DrawingContext context)
	{
		double size = SquareSize;
		double step = size * 2;
		double centerY = Bounds.Height / 2;
		for (int i = 0; i < Count; i++)
		{
			// Distance from the bouncing crest; squares near it grow and brighten.
			double crest = wavePhase <= Travel ? wavePhase : 2 * Travel - wavePhase;
			double distance = Math.Abs(i - (crest - 1.5));
			double k = Math.Clamp(1 - distance / 2.2, 0, 1);
			double side = size * (0.55 + 0.85 * k);
			double opacity = 0.30 + 0.70 * k;
			var brush = new SolidColorBrush(BaseColor, opacity);
			context.FillRectangle(brush, new Rect(
				i * step + (size - side) / 2, centerY - side / 2, side, side));
		}
	}
}
