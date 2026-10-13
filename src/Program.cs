using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Win32;

namespace StairGuard;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "Local\\DarkV-StairGuard-8e6d2d1e", out bool first);
        if (!first) return;
        Application.ThreadException += (_, e) => Log.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write(e.ExceptionObject);
        Application.Run(new TrayForm());
    }
}

internal static class Paths
{
    public const string Channel = "8e6d2d1e-4205-4759-b79e-f9e8c04549cc";
    public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StairGuard");
    public static readonly string Settings = Path.Combine(Home, "settings.json");
    public static readonly string Model = Path.Combine(Home, "yolov8n.onnx");
    public static readonly string Journal = Path.Combine(Home, "stairguard.log");
}

internal static class Log
{
    static readonly object Gate = new();
    public static void Write(object? data)
    {
        try
        {
            Directory.CreateDirectory(Paths.Home);
            lock (Gate)
            {
                var f = Paths.Journal;
                if (File.Exists(f) && new FileInfo(f).Length > 1_000_000) File.Move(f, f + ".old", true);
                File.AppendAllText(f, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {data}{Environment.NewLine}");
            }
        }
        catch { /* logging is best-effort */ }
    }
}

internal sealed class Settings
{
    public string Server { get; set; } = "http://10.1.60.188:8080";
    public string Login { get; set; } = "admin";
    public string Credential { get; set; } = "";
    // v1.5: independently configured camera/video stream and local AI rate.
    public string VideoPreset { get; set; } = VideoProfiles.Economy;
    public double AnalysisFps { get; set; } = 1.0;
    public bool MotionGateEnabled { get; set; } = true;
    public int Fps { get; set; } = 5;
    public int ResolutionX { get; set; } = 640;
    public int ResolutionY { get; set; } = 352;
    public int JpegQuality { get; set; } = 60;
    public int ConfidencePercent { get; set; } = 42;
    public int CooldownSeconds { get; set; } = 30;
    // All values are normalized fractions of the current camera image.
    public RectangleF? Region { get; set; }
    public PointF? ArrowFrom { get; set; }
    public PointF? ArrowTo { get; set; }

    public bool Ready => !string.IsNullOrWhiteSpace(Credential) && Region.HasValue && ArrowFrom.HasValue && ArrowTo.HasValue;

    public static Settings Load()
    {
        try
        {
            var config = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.Settings)) ?? new();
            // Existing settings.json from 1.4 has no VideoPreset. Adopt the safe
            // low-CPU preset while keeping credentials, camera ROI and arrow.
            VideoProfiles.Apply(config);
            config.AnalysisFps = AnalysisRates.Normalize(config.AnalysisFps);
            return config;
        }
        catch { return new(); }
    }
    public void Save()
    {
        VideoProfiles.Apply(this);
        AnalysisFps = AnalysisRates.Normalize(AnalysisFps);
        Directory.CreateDirectory(Paths.Home);
        File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (on) key?.SetValue("StairGuard", $"\"{Environment.ProcessPath}\"");
        else key?.DeleteValue("StairGuard", false);
    }
    public static bool Autostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("StairGuard") != null;
    }
}

// Native Windows DPAPI. Save the MD5 equivalent credential, never the plaintext password.
internal static class Vault
{
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Count; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr LocalFree(IntPtr ptr);

    static byte[] Transform(byte[] inputBytes, bool protect)
    {
        var input = new Blob { Count = inputBytes.Length, Data = Marshal.AllocHGlobal(inputBytes.Length) };
        try
        {
            Marshal.Copy(inputBytes, 0, input.Data, inputBytes.Length);
            Blob output;
            bool ok = protect
                ? CryptProtectData(ref input, "StairGuard credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] bytes = new byte[output.Count];
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
                return bytes;
            }
            finally { LocalFree(output.Data); }
        }
        finally {
            for (int i = 0; i < inputBytes.Length; ++i) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data); }
    }
    public static string Seal(string passwordOrHash)
    {
        var md5 = passwordOrHash.Length == 32 && passwordOrHash.All(Uri.IsHexDigit)
            ? passwordOrHash.ToLowerInvariant()
            : Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(passwordOrHash))).ToLowerInvariant();
        byte[] b = Encoding.UTF8.GetBytes(md5);
        try { return Convert.ToBase64String(Transform(b, true)); }
        finally { CryptographicOperations.ZeroMemory(b); }
    }
    public static string Open(string encrypted)
    {
        byte[] b = Transform(Convert.FromBase64String(encrypted), false);
        try { return Encoding.UTF8.GetString(b); }
        finally { CryptographicOperations.ZeroMemory(b); }
    }
}

// Macroscop 2.3 /mobile: verified HTTP 200, multipart MJPEG, ~2 FPS on this server.
internal static class Mjpeg
{
    static readonly HttpClient Http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false })
    { Timeout = Timeout.InfiniteTimeSpan };

    static string Url(Settings settings)
    {
        string baseUrl = settings.Server.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("Для локального сервера нужен корректный адрес http://...");
        string hash = Vault.Open(settings.Credential);
        return $"{baseUrl}/mobile?login={Uri.EscapeDataString(settings.Login)}&password={Uri.EscapeDataString(hash)}" +
               $"&channelid={Paths.Channel}&fps={settings.Fps}&resolutionx={settings.ResolutionX}&resolutiony={settings.ResolutionY}&quality={settings.JpegQuality}&withcontenttype=true";
    }

    public static async Task Consume(Settings config, Func<byte[], Task> onFrame, CancellationToken token)
    {
        // URLs include a password-equivalent hash. Never log full URLs or response headers.
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(config));
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new IOException($"Macroscop HTTP {(int)res.StatusCode}");
        using var stream = await res.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] chunk = new byte[16_384];
        using var jpeg = new MemoryStream();
        bool inside = false;
        int previous = -1;
        while (!token.IsCancellationRequested)
        {
            int count = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (count == 0) throw new IOException("Видеопоток был закрыт сервером");
            for (int i = 0; i < count; i++)
            {
                int b = chunk[i];
                if (!inside)
                {
                    if (previous == 0xff && b == 0xd8)
                    { inside = true; jpeg.SetLength(0); jpeg.WriteByte(0xff); jpeg.WriteByte(0xd8); }
                }
                else
                {
                    jpeg.WriteByte((byte)b);
                    if (jpeg.Length > 2_000_000) { inside = false; jpeg.SetLength(0); }
                    else if (previous == 0xff && b == 0xd9)
                    {
                        inside = false;
                        await onFrame(jpeg.ToArray()).ConfigureAwait(false);
                        jpeg.SetLength(0);
                    }
                }
                previous = b;
            }
        }
    }

    public static async Task<Bitmap> Grab(Settings settings, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(12));
        Bitmap? image = null;
        try
        {
            await Consume(settings, data =>
            {
                using var stream = new MemoryStream(data);
                using var original = Image.FromStream(stream);
                image = new Bitmap(original);
                linked.Cancel();
                return Task.CompletedTask;
            }, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (image != null) { }
        if (image == null) throw new IOException("Не удалось получить JPEG за 12 секунд");
        return image;
    }
}

 // Video presets are request parameters, not a promise that the server can deliver
// the requested resolution / rate. Macroscop may cap or rescale the stream.
internal static class VideoProfiles
{
    public const string Economy = "Экономичный";
    public const string Balanced = "Баланс";
    public const string Quality = "Качество";
    public static readonly string[] Names = { Economy, Balanced, Quality };

    public static void Apply(Settings s)
    {
        if (!Names.Contains(s.VideoPreset)) s.VideoPreset = Economy;
        switch (s.VideoPreset)
        {
            case Balanced:
                s.Fps = 10; s.ResolutionX = 640; s.ResolutionY = 352; s.JpegQuality = 75;
                break;
            case Quality:
                s.Fps = 10; s.ResolutionX = 1280; s.ResolutionY = 720; s.JpegQuality = 85;
                break;
            default:
                s.Fps = 5; s.ResolutionX = 640; s.ResolutionY = 352; s.JpegQuality = 60;
                break;
        }
    }

    public static string Describe(string name) => name switch
    {
        Balanced => "640×352 · 10 FPS · JPEG 75%",
        Quality => "1280×720 · 10 FPS · JPEG 85%",
        _ => "640×352 · 5 FPS · JPEG 60%"
    };
}

internal static class AnalysisRates
{
    public static readonly double[] Values = { 0.5, 1.0, 2.0 };
    public static double Normalize(double fps) => Values.MinBy(value => Math.Abs(value - fps));
    public static string Label(double fps) => fps.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " FPS";
}
