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
            if (!File.Exists(imagePath))
            {
                Log.Write($"Toast image missing: {imagePath}");
                return;
            }

            var toast = new ToastContentBuilder()
                .AddText("StairGuard")
                .AddText("Человек обнаружен на лестнице")
                .AddText($"Время: {time:HH:mm:ss}\nУверенность: {confidence:P0}")
                .AddHeroImage(new Uri(imagePath))
                .AddArgument("triggerImage", imagePath)
                .GetToastContent();

            ToastNotificationManagerCompat.CreateToastNotifier()
                .Show(toast.CreateToastNotification());
        }
        catch (Exception ex)
        {
            Log.Write($"Toast notification failed: {ex}");
        }
    }

    public static void OpenTrigger(string? imagePath)
    {
        try
        {
            string target = imagePath != null && File.Exists(imagePath)
                ? imagePath
                : Path.Combine(Paths.Home, "Triggers");

            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Write($"Open trigger failed: {ex}");
        }
    }
}
