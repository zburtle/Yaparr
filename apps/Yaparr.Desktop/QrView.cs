using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Yaparr.Desktop;

/// <summary>Draws a QR code: dark squares on white, whatever the theme, because that is what a camera reads. The matrix already has its blank border.</summary>
internal sealed class QrView : Control
{
    private bool[,]? _matrix;

    public bool[,]? Matrix
    {
        get => _matrix;
        set { _matrix = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds);
        if (_matrix == null) return;
        int n = _matrix.GetLength(0);
        // Whole device-independent pixels per square, so the edges stay sharp.
        double cell = Math.Max(1, Math.Floor(Math.Min(bounds.Width, bounds.Height) / n));
        double left = (bounds.Width - cell * n) / 2, top = (bounds.Height - cell * n) / 2;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                if (_matrix[y, x])
                    context.FillRectangle(Brushes.Black, new Rect(left + x * cell, top + y * cell, cell, cell));
    }
}
