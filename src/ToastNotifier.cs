using Microsoft.Toolkit.Uwp.Notifications;
using System.Diagnostics;
using Windows.UI.Notifications;

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

    public static void ShowTrigger(string imagePath, float confidence, DateTime time, bool isTest = false)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                Log.Write($"Toast image missing: {imagePath}");
                return;
            }

            var content = new ToastContentBuilder()
                .AddText("StairGuard")
                .AddText(isTest ? "Тестовое срабатывание" : "Человек обнаружен на лестнице")
                .AddText($"Время: {time:HH:mm:ss}\nУверенность: {confidence:P0}")
                .AddHeroImage(new Uri(imagePath))
                .AddArgument("triggerImage", imagePath)
                .GetToastContent();

            var notification = new ToastNotification(content.GetXml());
            ToastNotificationManager.CreateToastNotifier("StairGuard").Show(notification);
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
