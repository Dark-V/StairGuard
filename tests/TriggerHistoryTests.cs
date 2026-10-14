using System.Drawing;

namespace StairGuard;

internal static class Paths
{
    public static readonly string Home = Path.Combine(Path.GetTempPath(), "StairGuard-tests-" + Guid.NewGuid().ToString("N"));
    public static readonly string TriggersJournal = Path.Combine(Home, "triggers.log");
}

internal static class Log
{
    public static void Write(object? data) => Console.Error.WriteLine(data);
}

internal static class Program
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void CheckImage(string? path, int width, int height)
    {
        Check(path != null && File.Exists(path), "JPEG was not saved");
        using var image = Image.FromFile(path!);
        Check(image.Width == width && image.Height == height,
            $"Wrong crop: expected {width}x{height}, got {image.Width}x{image.Height}");
    }

    private static int Main()
    {
        try
        {
            using var source = new Bitmap(64, 48);
            using (var graphics = Graphics.FromImage(source))
                graphics.Clear(Color.CornflowerBlue);

            // Regression: saving an entire Bitmap must never call Bitmap.Clone.
            CheckImage(TriggerHistory.Add(0.79f, source, new Rectangle(0, 0, 64, 48)), 64, 48);
            CheckImage(TriggerHistory.Add(0.55f, source, new Rectangle(10, 8, 30, 20)), 30, 20);
            CheckImage(TriggerHistory.Add(0.69f, source, new Rectangle(60, 44, 30, 20)), 4, 4);
            CheckImage(TriggerHistory.Add(0.50f, source, new Rectangle(-500, -500, 10, 10)), 64, 48);
            CheckImage(TriggerHistory.Add(1f, source, isTest: true), 64, 48);
            Check(TriggerHistory.Add(1f, isTest: true) == null, "No frame must mean no JPEG");

            var rows = File.ReadAllLines(Paths.TriggersJournal);
            Check(rows.Length == 6, $"Expected six visits, found {rows.Length}");
            Check(rows.Count(line => line.Contains("ТЕСТ")) == 2, "Manual events must be labelled as tests");
            Check(rows.Count(line => line.Contains("ПОСЕЩЕНИЕ")) == 4, "Real events missing from journal");

            var entries = TriggerHistory.ReadRecent(10);
            Check(entries.Count == 6, $"Expected six parsed entries, found {entries.Count}");
            Check(entries[0].IsTest, "Newest entry must be test");
            Check(entries[0].Confidence > 0.99f, "Confidence parser failed");
            Check(entries[0].ImagePath == null, "No-image entry must parse without a path");
            Check(entries.Any(entry => !entry.IsTest && entry.ImagePath != null && File.Exists(entry.ImagePath)),
                "Visit image path parser failed");

            File.WriteAllText(Paths.TriggersJournal, "Срабатываний ещё не было.");
            TriggerHistory.Add(0.75f, source, isTest: false);
            Check(!File.ReadAllText(Paths.TriggersJournal).Contains("Срабатываний ещё не было."),
                "Legacy placeholder was not removed");
            Check(TriggerHistory.ReadRecent(5).Count == 1, "Migrated visit log must contain one parseable entry");

            Console.WriteLine("Trigger history image, migration and journal parser tests: PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            if (Directory.Exists(Paths.Home)) Directory.Delete(Paths.Home, recursive: true);
        }
    }
}
