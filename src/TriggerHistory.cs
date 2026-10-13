namespace StairGuard;

internal static class TriggerHistory
{
    static readonly object LockObj = new();

    public static void Add(float confidence, Bitmap? frame = null, Rectangle? crop = null)
    {
        try
        {
            Directory.CreateDirectory(Paths.Home);
            var dir = Path.Combine(Paths.Home, "Triggers");
            Directory.CreateDirectory(dir);

            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
            var image = Path.Combine(dir, stamp + ".jpg");

            if (frame != null && crop.HasValue)
            {
                using var cut = frame.Clone(crop.Value, frame.PixelFormat);
                cut.Save(image, System.Drawing.Imaging.ImageFormat.Jpeg);
            }

            var file = Path.Combine(Paths.Home, "triggers.log");
            lock (LockObj)
            {
                var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
                lines.Insert(0, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {confidence:P0} | {image}");
                File.WriteAllLines(file, lines.Take(5));
            }

            lock (LockObj)
            {
                foreach (var old in Directory.GetFiles(dir, "*.jpg")
                    .OrderByDescending(File.GetCreationTimeUtc).Skip(5))
                {
                    try { File.Delete(old); } catch { }
                }
            }
        }
        catch (Exception ex) { Log.Write(ex); }
    }
}
