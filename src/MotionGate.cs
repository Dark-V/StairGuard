using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StairGuard;

// Lightweight prefilter; samples only pixels inside the user's polygon.
// Does not run YOLO until motion is detected. No extra camera connection.
internal sealed class MotionGate
{
    const int Width = 96;
    const int Height = 64;
    const int PixelCount = Width * Height;

    readonly RectangleF bounds;
    readonly bool[] mask = new bool[PixelCount];
    readonly bool enabled;
    readonly int activePixels;
    byte[]? previous;

    public MotionGate(Settings settings)
    {
        var polygon = RegionGeometry.Resolve(settings);
        if (polygon is null) return;
        bounds = RegionGeometry.Bounds(polygon);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        for (int y = 0; y < Height; ++y)
        for (int x = 0; x < Width; ++x)
        {
            float nx = bounds.Left + (x + .5f) / Width * bounds.Width;
            float ny = bounds.Top + (y + .5f) / Height * bounds.Height;
            if (RegionGeometry.Contains(polygon, nx, ny))
            {
                mask[y * Width + x] = true;
                ++activePixels;
            }
        }
        enabled = activePixels >= 8;
    }

    public bool HasMovement(Bitmap frame)
    {
        if (!enabled) return true;
        int left = Math.Clamp((int)(bounds.Left * frame.Width), 0, frame.Width - 1);
        int top = Math.Clamp((int)(bounds.Top * frame.Height), 0, frame.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(bounds.Right * frame.Width), left + 1, frame.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(bounds.Bottom * frame.Height), top + 1, frame.Height);
        if (right - left < 5 || bottom - top < 5) return true;

        using var sample = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(sample))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(frame, new Rectangle(0, 0, Width, Height),
                Rectangle.FromLTRB(left, top, right, bottom), GraphicsUnit.Pixel);
        }
        var bits = sample.LockBits(new Rectangle(0, 0, Width, Height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var current = new byte[PixelCount];
        try
        {
            byte[] rgb = new byte[bits.Stride * Height];
            Marshal.Copy(bits.Scan0, rgb, 0, rgb.Length);
            for (int y = 0; y < Height; ++y)
            for (int x = 0; x < Width; ++x)
            {
                int i = y * bits.Stride + x * 3;
                current[y * Width + x] =
                    (byte)((rgb[i] * 29 + rgb[i + 1] * 150 + rgb[i + 2] * 77) >> 8);
            }
        }
        finally { sample.UnlockBits(bits); }

        byte[]? old = previous;
        previous = current;
        if (old == null) return false;

        int sum = 0;
        for (int i = 0; i < PixelCount; ++i)
            if (mask[i]) sum += current[i] - old[i];
        float mean = sum / (float)activePixels;
        int changed = 0;
        for (int i = 0; i < PixelCount; ++i)
            if (mask[i] && Math.Abs(current[i] - old[i] - mean) > 22)
                ++changed;

        return changed >= Math.Max(8, (int)(activePixels * .008f));
    }
}
