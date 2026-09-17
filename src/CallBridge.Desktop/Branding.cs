using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CallBridge.Desktop;

/// <summary>White-label branding: a validated customer logo and accent color shared by every window.</summary>
public static class Branding
{
    public const string DefaultAccent = "#1F6FD1";
    public const long MaxLogoBytes = 1024 * 1024;
    public const int MaxLogoPixels = 2048;
    private static readonly string[] LogoExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>The logo currently shown in CallBridge, or null to show the product initials.</summary>
    public static ImageSource? Logo { get; private set; }

    public static string ProductInitials { get; private set; } = "CB";

    public static string ProductName { get; private set; } = "CallBridge";

    public static event Action? Changed;

    public static string LogoFolder
    {
        get
        {
            var isolatedRoot = Environment.GetEnvironmentVariable("CALLBRIDGE_SETTINGS_ROOT");
            var root = string.IsNullOrWhiteSpace(isolatedRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IT Health Technologies", "CallBridge")
                : Path.GetFullPath(isolatedRoot);
            return Path.Combine(root, "branding");
        }
    }

    public static void Apply(string productName, string? logoPath, string? accent)
    {
        ProductName = string.IsNullOrWhiteSpace(productName) ? "CallBridge" : productName.Trim();
        ProductInitials = MainWindow.LogoInitials(productName);
        Logo = LoadLogo(logoPath);
        ThemePalette.SetAccent(TryNormalizeAccent(accent, out var normalized, out _) ? normalized : DefaultAccent);
        Changed?.Invoke();
    }

    /// <summary>Accepts #RRGGBB colors dark enough for white button text (contrast of at least 3:1).</summary>
    public static bool TryNormalizeAccent(string? value, out string normalized, out string message)
    {
        normalized = DefaultAccent;
        message = "";
        var text = (value ?? "").Trim();
        if (text.Length == 0) return true;
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length != 7 || !text[1..].All(Uri.IsHexDigit))
        {
            message = "Enter the accent color as six hex digits, like #1F6FD1.";
            return false;
        }
        var color = (Color)ColorConverter.ConvertFromString(text);
        if (ContrastWithWhite(color) < 3.0)
        {
            message = "That color is too light for white button text. Choose a darker accent.";
            return false;
        }
        normalized = text.ToUpperInvariant();
        return true;
    }

    public static double ContrastWithWhite(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        return 1.05 / (luminance + 0.05);
    }

    /// <summary>Validates a picked logo and copies it into CallBridge's branding folder. Returns the stored path.</summary>
    public static string ImportLogo(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!LogoExtensions.Contains(extension)) throw new InvalidDataException("Choose a PNG or JPEG logo.");
        var info = new FileInfo(sourcePath);
        if (!info.Exists) throw new FileNotFoundException("The logo file wasn't found.", sourcePath);
        if (info.Length > MaxLogoBytes) throw new InvalidDataException("The logo must be 1 MB or smaller.");

        var bytes = File.ReadAllBytes(sourcePath);
        using (var stream = new MemoryStream(bytes))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.FirstOrDefault() ?? throw new InvalidDataException("The logo image couldn't be read.");
            if (frame.PixelWidth > MaxLogoPixels || frame.PixelHeight > MaxLogoPixels)
                throw new InvalidDataException($"The logo must be {MaxLogoPixels} × {MaxLogoPixels} pixels or smaller.");
        }

        Directory.CreateDirectory(LogoFolder);
        foreach (var old in LogoExtensions.Select(ext => Path.Combine(LogoFolder, "logo" + ext)).Where(File.Exists))
            File.Delete(old);
        var destination = Path.Combine(LogoFolder, "logo" + extension);
        File.WriteAllBytes(destination, bytes);
        return destination;
    }

    public static void RemoveLogo(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath)) return;
        var full = Path.GetFullPath(logoPath);
        if (!full.StartsWith(Path.GetFullPath(LogoFolder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(full)) File.Delete(full);
    }

    public static ImageSource? LoadLogo(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath) || !File.Exists(logoPath)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelHeight = 96;
            image.UriSource = new Uri(Path.GetFullPath(logoPath));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException or FileFormatException)
        {
            App.LogStartup($"Branding logo warning: {ex.GetType().Name}");
            return null;
        }
    }
}
