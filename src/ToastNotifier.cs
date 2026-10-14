using CommunityToolkit.WinUI.Notifications;

namespace StairGuard;

internal static class ToastNotifier
{
    public static bool ShowTrigger(string imagePath, float confidence, DateTime time, Settings settings, bool isTest = false)
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
            var toast = new ToastContentBuilder()
                .AddText("StairGuard")
                .AddText(isTest ? "Тестовое срабатывание" : "Подъём по лестнице")
                .AddText($"Время: {time:HH:mm:ss}   Уверенность: {confidence:P0}")
                .AddHeroImage(new Uri(imagePath, UriKind.Absolute));

            // Prevent duplicate playback: Windows handles the native default,
            // while presets and user files are played separately.
            if (NotificationAudio.UsesExternalPlayback(settings.NotificationSound))
                toast.AddAudio(new ToastAudio { Silent = true });

            toast.Show();
            if (NotificationAudio.UsesExternalPlayback(settings.NotificationSound))
                NotificationAudio.Play(settings.NotificationSound, settings.CustomSoundPath);

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
