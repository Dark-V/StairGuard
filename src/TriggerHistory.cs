using System.Drawing;
using System.Drawing.Imaging;

namespace StairGuard;

// Real visits and explicit test events share one durable, append-only journal.
// Failures to save an image are diagnostic errors, not lost visits.
internal static class TriggerHistory
{
    static readonly object Gate = new();

    public static string? Add(float confidence, Bitmap? frame = null, Rectangle? crop = null, bool isTest = false)
    {
        var time = DateTime.Now;
        string? imagePath = null;

        if (frame != null)
        {
            try
            {
                string dir = Path.Combine(Paths.Home, "Triggers");
                Directory.CreateDirectory(dir);
                var bounds = new Rectangle(Point.Empty, frame.Size);
                var requested = crop ?? bounds;
                var region = Rectangle.Intersect(bounds, requested);
                if (region.Width <= 0 || region.Height <= 0)
                {
                    Log.Write("Область снимка вне кадра; сохранён полный кадр.");
                    region = bounds;
                }

                var path = Path.Combine(dir, $"{time:yyyy-MM-dd_HH-mm-ss-fff}-{Guid.NewGuid():N}.jpg");
                if (region == bounds)
                {
                    // The old full-frame Bitmap.Clone(rect, PixelFormat) sometimes threw
                    // ArgumentException. A second clone is unnecessary for a JPEG.
                    frame.Save(path, ImageFormat.Jpeg);
                }
                else
                {
                    // Rendering a clipped crop to a fresh RGB bitmap is safe for
                    // unusual source pixel formats and partially off-screen boxes.
                    using var cut = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
                    using (var graphics = Graphics.FromImage(cut))
                        graphics.DrawImage(frame, new Rectangle(Point.Empty, cut.Size), region, GraphicsUnit.Pixel);
                    cut.Save(path, ImageFormat.Jpeg);
                }
                imagePath = path;
            }
            catch (Exception ex)
            {
                Log.Write($"Снимок триггера: {ex}");
            }
        }

        try
        {
            Directory.CreateDirectory(Paths.Home);
            var kind = isTest ? "ТЕСТ" : "ПОСЕЩЕНИЕ";
            var photo = imagePath ?? "снимок отсутствует";
            var line = $"[{time:yyyy-MM-dd HH:mm:ss}] {kind} | уверенность {confidence:P0} | {photo}";
            lock (Gate)
                File.AppendAllText(Paths.TriggersJournal, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log.Write($"Журнал посещений: {ex}");
        }

        return imagePath;
    }
}
