using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace StairGuard;

// Real visits and explicit test events share one durable, append-only journal.
// Failures to save an image are diagnostic errors, not lost visits.
internal sealed record TriggerEntry(DateTime Time, bool IsTest, float Confidence, string? ImagePath);

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
            {
                // Migrate the placeholder written by old builds. It had no trailing
                // newline and caused the first real event to be glued to that text.
                if (File.Exists(Paths.TriggersJournal))
                {
                    var existing = File.ReadAllText(Paths.TriggersJournal);
                    if (existing.Contains("Срабатываний ещё не было.", StringComparison.Ordinal))
                        File.WriteAllText(Paths.TriggersJournal,
                            existing.Replace("Срабатываний ещё не было.", "", StringComparison.Ordinal).TrimStart());
                }
                File.AppendAllText(Paths.TriggersJournal, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Журнал посещений: {ex}");
        }

        return imagePath;
    }

    public static IReadOnlyList<TriggerEntry> ReadRecent(int max = 200)
    {
        if (max <= 0) return Array.Empty<TriggerEntry>();
        try
        {
            lock (Gate)
            {
                if (!File.Exists(Paths.TriggersJournal)) return Array.Empty<TriggerEntry>();
                var result = new List<TriggerEntry>(Math.Min(max, 200));
                foreach (var line in File.ReadLines(Paths.TriggersJournal).Reverse())
                {
                    if (TryParse(line, out var entry))
                        result.Add(entry);
                    if (result.Count >= max) break;
                }
                return result;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Чтение журнала посещений: {ex}");
            return Array.Empty<TriggerEntry>();
        }
    }

    static bool TryParse(string line, out TriggerEntry entry)
    {
        entry = new TriggerEntry(default, false, 0, null);
        if (string.IsNullOrWhiteSpace(line)) return false;

        int close = line.IndexOf(']');
        if (line.Length < 22 || line[0] != '[' || close < 2) return false;
        if (!DateTime.TryParseExact(line[1..close], "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return false;

        var parts = line[(close + 1)..].Trim().Split(" | ", StringSplitOptions.None);
        if (parts.Length < 2) return false;
        bool isTest = parts[0].Equals("ТЕСТ", StringComparison.OrdinalIgnoreCase);

        string confidenceText = parts[1]
            .Replace("уверенность", "", StringComparison.OrdinalIgnoreCase)
            .Replace("%", "", StringComparison.Ordinal)
            .Trim()
            .Replace(',', '.');
        if (!float.TryParse(confidenceText, NumberStyles.Float, CultureInfo.InvariantCulture, out float percent))
            percent = 0;

        string? imagePath = parts.Length >= 3 ? parts[2].Trim() : null;
        if (string.IsNullOrWhiteSpace(imagePath) ||
            imagePath.Equals("снимок отсутствует", StringComparison.OrdinalIgnoreCase))
            imagePath = null;

        entry = new TriggerEntry(time, isTest, Math.Clamp(percent / 100f, 0f, 1f), imagePath);
        return true;
    }
}
