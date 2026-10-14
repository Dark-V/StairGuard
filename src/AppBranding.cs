using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;

namespace StairGuard;

// The original assets are compiled into StairGuard.exe. No loose files are
// required for the tray icon. Windows Toast APIs need a local file URI for
// the app logo, so the embedded PNG is extracted once to LocalAppData.
internal static class AppBranding
{
    const string IconResource = "StairGuard.Assets.StairGuard.ico";
    const string LogoResource = "StairGuard.Assets.StairGuard.png";

    public static Icon LoadIcon()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(IconResource)
            ?? throw new InvalidOperationException("StairGuard icon resource is missing.");
        return new Icon(resource);
    }

    public static string EnsureToastLogo()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(LogoResource)
            ?? throw new InvalidOperationException("StairGuard toast logo is missing.");

        using var copy = new MemoryStream();
        resource.CopyTo(copy);
        byte[] embedded = copy.ToArray();
        string folder = Path.Combine(Paths.Home, "Assets");
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, "StairGuard.png");

        if (File.Exists(destination))
        {
            byte[] existingHash = SHA256.HashData(File.ReadAllBytes(destination));
            byte[] embeddedHash = SHA256.HashData(embedded);
            if (CryptographicOperations.FixedTimeEquals(existingHash, embeddedHash))
                return destination;
        }

        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, embedded);
            File.Move(temp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return destination;
    }
}
