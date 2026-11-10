using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Yaparr.Desktop;

/// <summary>
/// A lake freighter that crosses the empty chat screen, and the message box while it is empty and not being typed in, on November 10th, the day the SS Edmund Fitzgerald was lost on Lake Superior
/// in 1975 with all 29 of her crew. She is a quiet silhouette that fades in and out of the dark and rides the swell, and makes no sound.
/// Clicking the ship raises <see cref="ShipClicked"/> (the window offers to open Gordon Lightfoot's song in the browser). It draws only
/// while it is visible, so the rest of the year it costs nothing.
/// </summary>
internal sealed class FitzgeraldShip : Control
{
    // One pass takes this long, then she is gone for a while before she comes again.
    private const double CrossingSeconds = 80, PauseSeconds = 12;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private bool _attached;
    private readonly DateTime _start = DateTime.UtcNow;

    public FitzgeraldShip()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        _timer.Tick += (_, _) => InvalidateVisual();
    }

    /// <summary>A small ship for a single line, such as the message box: sized to the height it is given, with one line of swell.</summary>
    public bool Compact { get; set; }

    /// <summary>The ship itself was clicked (not the water or the empty space around her).</summary>
    public event Action? ShipClicked;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        e.Handled = true;
        ShipClicked?.Invoke();
    }

    /// <summary>Gordon Lightfoot's song, which clicking the ship offers to open.</summary>
    public const string SongUrl = "https://youtu.be/FuzTkGyxkYI";

    /// <summary>True on November 10th, by the computer's own calendar. YAPARR_FITZ=1 shows it on any day, to look at it.</summary>
    public static bool IsHerDay(DateTime today) =>
        (today.Month == 11 && today.Day == 10) || Environment.GetEnvironmentVariable("YAPARR_FITZ") == "1";

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (IsEffectivelyVisible) _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        if (IsVisible && _attached) _timer.Start(); else _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 80 || h < 30) return;

        double t = (DateTime.UtcNow - _start).TotalSeconds;
        double cycle = CrossingSeconds + PauseSeconds;
        double progress = (t % cycle) / CrossingSeconds;           // 0 to 1 across; above 1 she is out of sight
        if (progress > 1) return;

        var brush = this.TryFindResource("Muted", ActualThemeVariant, out var found) && found is IBrush b ? b : Brushes.Gray;

        // She is about a third of the width long, and no bigger than is quiet to look at.
        double scale = Compact ? Math.Clamp((h - 10) / 32.0, 0.5, 1.4) : Math.Clamp(w / 3.0 / 100.0, 1.2, 3.2);
        double length = 101 * scale;
        double x = -length + progress * (w + length);
        double waterline = Compact ? h - 7 : h * 0.72;

        // Fade in as she comes out of the dark at one side, and out at the other.
        double edge = Math.Min(progress, 1 - progress);
        double fade = Math.Clamp(edge / 0.12, 0, 1);

        // Only where the ship is can be clicked: a clear rectangle over her, so the water and the space around her let clicks through.
        // She cannot be clicked while she is still fading in or out at the edge.
        if (fade > 0.3) context.DrawRectangle(Brushes.Transparent, null, new Rect(x, waterline - 30 * scale, length, 32 * scale));

        using (context.PushOpacity(0.55 * fade))
        {
            double bob = Math.Sin(t * 0.7) * 1.2 * scale / 2;
            double roll = Math.Sin(t * 0.5 + 1) * 0.5;               // degrees
            var pivot = new Point(x + length / 2, waterline);
            var transform = Matrix.CreateTranslation(-pivot.X, -pivot.Y) * Matrix.CreateRotation(roll * Math.PI / 180) * Matrix.CreateTranslation(pivot.X, pivot.Y + bob);
            using (context.PushTransform(transform))
                DrawShip(context, brush, x, waterline, scale);
        }

        // The swell: a few long, slow lines, each a little out of step with the last.
        for (int row = 0; row < (Compact ? 1 : 3); row++)
        {
            double y = waterline + 3 + row * 7 * Math.Max(1, scale / 2);
            var pen = new Pen(brush, 1);
            using (context.PushOpacity((0.28 - row * 0.07) * (row == 0 ? 1 : 0.9)))
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    double step = 6;
                    g.BeginFigure(new Point(0, y + Wave(0, t, row)), false);
                    for (double px = step; px <= w; px += step) g.LineTo(new Point(px, y + Wave(px, t, row)));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }

    private static double Wave(double px, double t, int row) => Math.Sin(px / 38.0 + t * (0.6 + row * 0.15) + row * 1.7) * (1.4 + row * 0.5);

    // A laker seen from the side, bow to the right: the pilothouse right forward, the engines and the stack right aft, and a long open
    // deck between. Coordinates are in hundredths of her length; y counts down from the deck, and the waterline is the origin.
    private static void DrawShip(DrawingContext context, IBrush brush, double left, double waterline, double s)
    {
        Point P(double px, double py) => new(left + px * s, waterline + py * s);

        // Hull, down to the waterline.
        var hull = new StreamGeometry();
        using (var g = hull.Open())
        {
            g.BeginFigure(P(0, -9), true);
            g.LineTo(P(94, -10));
            g.LineTo(P(101, -12));      // the bow rises
            g.LineTo(P(98, 0));
            g.LineTo(P(4, 0));
            g.LineTo(P(0, -3));
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, hull);

        // Cabins and the pilothouse at the bow; the engine house and stack at the stern.
        context.DrawRectangle(brush, null, new Rect(P(84, -17), P(95, -10)));       // forward cabins
        context.DrawRectangle(brush, null, new Rect(P(86, -21), P(93, -17)));       // pilothouse
        context.DrawRectangle(brush, null, new Rect(P(3, -17), P(17, -9)));         // engine house
        context.DrawRectangle(brush, null, new Rect(P(8.5, -24), P(12.5, -17)));    // stack
        context.DrawRectangle(brush, null, new Rect(P(8, -25.5), P(13, -24)));      // its cap
        context.DrawLine(new Pen(brush, Math.Max(1, s * 0.4)), P(89.5, -21), P(89.5, -28));   // mast
    }
}
