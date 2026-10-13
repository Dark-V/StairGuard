using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StairGuard;

// Low-cost motion gate. Compare a 96x64 grayscale thumbnail of the configured
// stair ROI, removing global brightness shifts before measuring changed pixels.
// This is only a prefilter: YOLO must still identify a person and the tracker
// must still establish the climbing direction.
internal sealed class MotionGate
{
    const int Width = 96;
    const int Height = 64;
    const int PixelCount = Width * Height;
    byte[]? previous;

    public bool HasMovement(Bitmap frame, RectangleF? normalizedRegion)
    {
        if (!normalizedRegion.HasValue) return true;
        var r = normalizedRegion.Value;
        int left = Math.Clamp((int)(r.Left * frame.Width), 0, frame.Width - 1);
        int top = Math.Clamp((int)(r.Top * frame.Height), 0, frame.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(r.Right * frame.Width), left + 1, frame.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(r.Bottom * frame.Height), top + 1, frame.Height);
        if (right - left < 5 || bottom - top < 5) return true;

        using var sample = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(sample))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(frame, new Rectangle(0, 0, Width, Height),
                Rectangle.FromLTRB(left, top, right, bottom), GraphicsUnit.Pixel);
        }
        var pixels = sample.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var current = new byte[PixelCount];
        try
        {
            var data = new byte[pixels.Stride * Height];
            Marshal.Copy(pixels.Scan0, data, 0, data.Length);
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = y * pixels.Stride + x * 3;
                current[y * Width + x] = (byte)((data[i] * 29 + data[i + 1] * 150 + data[i + 2] * 77) >> 8);
            }
        }
        finally { sample.UnlockBits(pixels); }

        byte[]? old = previous;
        previous = current;
        if (old == null) return false; // establish initial baseline

        int sum = 0;
        for (int i = 0; i < PixelCount; i++) sum += current[i] - old[i];
        int meanBrightnessDelta = sum / PixelCount;
        int changed = 0;
        for (int i = 0; i < PixelCount; i++)
        {
            // Ignore compression noise and scene-wide light changes.
            if (Math.Abs((current[i] - old[i]) - meanBrightnessDelta) > 22) ++changed;
        }
        return changed >= 48; // 0.78% of pixels in ROI
    }
}
