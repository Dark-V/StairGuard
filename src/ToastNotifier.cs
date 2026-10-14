using CommunityToolkit.WinUI.Notifications;
using System.Text.Json;

namespace StairGuard;

// Real desktop Win32 toast notifications are posted through the Windows
// notification platform and removed from its Action Center history by tag.
// ExpirationTime alone does NOT guarantee Action Center removal on Windows.
internal static class ToastNotifier
{
    private const string DetectionGroup = "StairGuard.Detection";
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    private static readonly object Gate = new();
    private static List<ScheduledRemoval>? removals;
    private static System.Windows.Forms.Timer? cleanupTimer;

    private sealed record ScheduledRemoval(string Tag, DateTimeOffset RemoveAfterUtc);

    // Called on the main WinForms thread. Never start one timer per alert.
    public static void StartCleanup()
    {
        if (cleanupTimer is not null) return;
        lock (Gate) removals = ReadSchedule();
        cleanupTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        cleanupTimer.Tick += (_, _) => RemoveExpired();
        cleanupTimer.Start();
        RemoveExpired(); // also handles expired alerts from previous app runs
    }

    public static void StopCleanup()
    {
        cleanupTimer?.Stop();
        cleanupTimer?.Dispose();
        cleanupTimer = null;
    }

    public static bool ShowTrigger(string imagePath, float confidence, DateTime time, Settings settings, bool isTest = false)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                Log.Write($"Windows notification image missing: {imagePath}");
                return false;
            }

            string logoPath = AppBranding.EnsureToastLogo();
            var toast = new ToastContentBuilder()
                .AddText("StairGuard")
                .AddText(isTest ? "Тестовое срабатывание" : "Подъём по лестнице")
                .AddText($"Время: {time:HH:mm:ss}   Уверенность: {confidence:P0}")
                .AddAppLogoOverride(new Uri(logoPath, UriKind.Absolute))
                .AddHeroImage(new Uri(imagePath, UriKind.Absolute));

            if (NotificationAudio.UsesExternalPlayback(settings.NotificationSound))
                toast.AddAudio(new ToastAudio { Silent = true });

            if (isTest)
            {
                // Manual tests must not affect the life cycle of real alerts.
                toast.Show();
            }
            else
            {
                string tag = Guid.NewGuid().ToString("N")[..16];
                DateTimeOffset removeAfter = DateTimeOffset.UtcNow.Add(Retention);
                toast.Show(notification =>
                {
                    notification.Tag = tag;
                    notification.Group = DetectionGroup;
                    notification.ExpirationTime = removeAfter;
                });
                Remember(tag, removeAfter);
            }

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

    private static void Remember(string tag, DateTimeOffset removeAfter)
    {
        lock (Gate)
        {
            removals ??= ReadSchedule();
            removals.Add(new ScheduledRemoval(tag, removeAfter));
            SaveSchedule(removals);
        }
    }

    private static void RemoveExpired()
    {
        lock (Gate)
        {
            removals ??= ReadSchedule();
            if (removals.Count == 0) return;
            var now = DateTimeOffset.UtcNow;
            bool changed = false;
            for (int i = removals.Count - 1; i >= 0; --i)
            {
                var item = removals[i];
                if (item.RemoveAfterUtc > now) continue;
                try
                {
                    // Windows API: remove only this StairGuard detection toast
                    // from Action Center, not the user's other notifications.
                    ToastNotificationManagerCompat.History.Remove(item.Tag, DetectionGroup);
                    removals.RemoveAt(i);
                    changed = true;
                }
                catch (Exception ex)
                {
                    // Retain the entry so it can be retried at next timer tick
                    // or after a restart.
                    Log.Write($"Удаление Windows Toast {item.Tag}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            if (changed) SaveSchedule(removals);
        }
    }

    private static List<ScheduledRemoval> ReadSchedule()
    {
        try
        {
            if (!File.Exists(Paths.ToastSchedule)) return new();
            var stored = JsonSerializer.Deserialize<List<ScheduledRemoval>>(
                File.ReadAllText(Paths.ToastSchedule));
            return stored?.Where(e => !string.IsNullOrWhiteSpace(e.Tag)
                && e.Tag.Length <= 16).TakeLast(500).ToList() ?? new();
        }
        catch (Exception ex)
        {
            Log.Write($"Загрузка расписания уведомлений: {ex.GetType().Name}");
            return new();
        }
    }

    private static void SaveSchedule(List<ScheduledRemoval> items)
    {
        try
        {
            Directory.CreateDirectory(Paths.Home);
            string temp = Paths.ToastSchedule + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(items));
            File.Move(temp, Paths.ToastSchedule, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Сохранение расписания уведомлений: {ex.GetType().Name}");
        }
    }
}
