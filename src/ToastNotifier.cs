using CommunityToolkit.WinUI.Notifications;

namespace StairGuard;

internal static class ToastNotifier
{
    public static bool ShowTrigger(string imagePath, float confidence, DateTime time, bool isTest = false)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                Log.Write($"Windows notification image missing: {imagePath}");
                return false;
            }

            // ToastContentBuilder.Show() uses ToastNotificationManagerCompat
            // internally, which registers identity-less Win32/WinForms apps.
            new ToastContentBuilder()
                .AddText("StairGuard")
                .AddText(isTest ? "Тестовое срабатывание" : "Человек обнаружен на лестнице")
                .AddText($"Время: {time:HH:mm:ss}   Уверенность: {confidence:P0}")
                .AddHeroImage(new Uri(imagePath, UriKind.Absolute))
                .Show();

            if (isTest) Log.Write("Тестовое Windows-уведомление отправлено.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Windows notification failed: {ex}");
            return false;
        }
    }
}
