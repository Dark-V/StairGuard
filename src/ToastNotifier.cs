using Microsoft.Toolkit.Uwp.Notifications;
using System.Diagnostics;

namespace StairGuard;

internal static class ToastNotifier
{
    public static string SaveTriggerImage(byte[] jpeg, DateTime time)
    {
        string dir = Path.Combine(Paths.Home, "Triggers");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{time:yyyy-MM-dd_HH-mm-ss}.jpg");
        File.WriteAllBytes(file, jpeg);
        return file;
    }

    public static void ShowTrigger(string imagePath, float confidence, DateTime time)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("StairGuard")
                .AddText("Обнаружен человек на лестнице")
                .AddText($"Время: {time:HH:mm:ss}\nУверенность: {confidence:P0}")
                .AddInlineImage(new Uri(imagePath))
                .AddArgument("trigger", imagePath)
                .Show();
        }
        catch (Exception ex)
        {
            Log.Write($"Toast notification failed: {ex.Message}");
        }
    }

    public static void OpenTrigger(string? imagePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = imagePath != null && File.Exists(imagePath)
                    ? imagePath
                    : Path.Combine(Paths.Home, "Triggers"),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write($"Open trigger failed: {ex.Message}");
        }
    }
}
