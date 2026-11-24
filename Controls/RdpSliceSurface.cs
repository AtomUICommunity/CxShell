using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace CxShell.Controls;

/// <summary>
/// Renders a rectangular slice of a shared RDP <see cref="WriteableBitmap"/> with zero extra pixel copies.
/// </summary>
public sealed class RdpSliceSurface : Control
{
    private Bitmap? _source;
    private Rect _sourceRect;

    public Bitmap? Source
    {
        get => _source;
        set
        {
            if (ReferenceEquals(_source, value))
                return;
            _source = value;
            InvalidateVisual();
        }
    }

    public Rect SourceRect
    {
        get => _sourceRect;
        set
        {
            if (_sourceRect == value)
                return;
            _sourceRect = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (_source == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var bitmapSize = _source.PixelSize;
        if (bitmapSize.Width <= 0 || bitmapSize.Height <= 0)
            return;

        var src = _sourceRect.Width > 0 && _sourceRect.Height > 0
            ? _sourceRect
            : new Rect(0, 0, bitmapSize.Width, bitmapSize.Height);

        if (src.X >= bitmapSize.Width || src.Y >= bitmapSize.Height)
        {
            context.FillRectangle(Brushes.Black, new Rect(0, 0, Bounds.Width, Bounds.Height));
            return;
        }

        var clampedX = System.Math.Clamp(src.X, 0, bitmapSize.Width - 1);
        var clampedY = System.Math.Clamp(src.Y, 0, bitmapSize.Height - 1);
        var clampedW = System.Math.Clamp(src.Width, 1, bitmapSize.Width - clampedX);
        var clampedH = System.Math.Clamp(src.Height, 1, bitmapSize.Height - clampedY);

        // Guard against bilinear edge bleeding from the adjacent monitor's right/bottom border
        if (clampedX > 0 && clampedW > 2)
        {
            clampedX += 1;
            clampedW -= 1;
        }
        if (clampedY > 0 && clampedH > 2)
        {
            clampedY += 1;
            clampedH -= 1;
        }

        context.DrawImage(
            _source,
            new Rect(clampedX, clampedY, clampedW, clampedH),
            new Rect(0, 0, Bounds.Width, Bounds.Height));
    }
}
