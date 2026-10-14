using System.Media;
using NAudio.Wave;

namespace StairGuard;

// Sound selection is independent of Windows toast rendering. Windows presets
// use the current sound scheme; custom audio is played through NAudio because
// unpackaged Win32 toasts cannot reliably play arbitrary user media paths.
internal static class NotificationAudio
{
    public const string Default = "Windows: стандартный";
    public const string Error = "Windows: ошибка";
    public const string Warning = "Windows: предупреждение";
    public const string Information = "Windows: информация";
    public const string Beep = "Windows: сигнал";
    public const string None = "Без звука";
    public const string Custom = "Свой файл";

    public static readonly string[] Modes = { Default, Error, Warning, Information, Beep, None, Custom };

    public static string Normalize(string? mode) => Modes.Contains(mode) ? mode! : Default;
    public static bool UsesExternalPlayback(string? mode) => Normalize(mode) != Default;

    static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".wma", ".m4a", ".aif", ".aiff" };

    public static bool IsSupportedFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Extensions.Contains(Path.GetExtension(path));

    public static string ImportCustomSound(string source)
    {
        if (!IsSupportedFile(source))
            throw new InvalidOperationException("Поддерживаются WAV, MP3, WMA, M4A, AIF и AIFF.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Выбранный аудиофайл не найден.", source);

        const long maxSize = 20L * 1024 * 1024;
        if (new FileInfo(source).Length > maxSize)
            throw new InvalidOperationException("Максимальный размер аудиофайла — 20 МБ.");

        var dir = Path.Combine(Paths.Home, "Audio");
        Directory.CreateDirectory(dir);
        if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(source)!),
            Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
            return source;

        // Do not depend on the original path (e.g. Downloads or USB drive).
        string target = Path.Combine(dir, "notification-" + Guid.NewGuid().ToString("N") +
            Path.GetExtension(source).ToLowerInvariant());
        File.Copy(source, target, overwrite: false);
        return target;
    }

    public static void Play(string? mode, string? customPath)
    {
        var selected = Normalize(mode);
        if (selected == None) return;

        // Never block the camera's inference loop or WinForms UI during playback.
        _ = Task.Run(() =>
        {
            try
            {
                switch (selected)
                {
                    case Default:
                    case Information:
                        SystemSounds.Asterisk.Play();
                        break;
                    case Error:
                        SystemSounds.Hand.Play();
                        break;
                    case Warning:
                        SystemSounds.Exclamation.Play();
                        break;
                    case Beep:
                        SystemSounds.Beep.Play();
                        break;
                    case Custom:
                        PlayFile(customPath);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Звук уведомления: {ex}");
            }
        });
    }

    static void PlayFile(string? path)
    {
        if (!IsSupportedFile(path) || !File.Exists(path))
            throw new FileNotFoundException("Настроенный звук уведомления недоступен.", path);

        // AudioFileReader uses Media Foundation for formats such as WMA/M4A
        // on supported Windows installations.
        using var reader = new AudioFileReader(path!);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();

        // Limit long files so an accidental hour-long MP3 cannot monopolize
        // the audio device or leave an indefinitely running playback task.
        var end = Environment.TickCount64 + 30_000;
        while (output.PlaybackState == PlaybackState.Playing && Environment.TickCount64 < end)
            Thread.Sleep(50);
        output.Stop();
    }
}
