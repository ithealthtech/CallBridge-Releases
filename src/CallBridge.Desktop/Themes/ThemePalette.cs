using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace CallBridge.Desktop;

/// <summary>
/// Light and dark palettes from the approved mockup. The palette follows the Windows app theme
/// (Settings > Personalization > Colors) and switches live when that setting changes.
/// Set CALLBRIDGE_THEME to "dark" or "light" to force one.
/// </summary>
internal static class ThemePalette
{
    private static readonly Dictionary<string, string> Dark = new()
    {
        ["Ground"] = "#0F1419", ["Panel"] = "#1A2129", ["Sunk"] = "#141A20", ["Chrome"] = "#222A33",
        ["Line"] = "#2C3540", ["LineStrong"] = "#3D4856", ["Ink"] = "#E6EBF0", ["Muted"] = "#95A1AD",
        ["Accent"] = "#1F6FD1", ["AccentHover"] = "#3A82E0", ["AccentSoft"] = "#1B2C44",
        ["Good"] = "#3FB57A", ["Warn"] = "#E0A443", ["WarnSoft"] = "#3A2E1A", ["Bad"] = "#E5645A",
        ["HoverOverlay"] = "#FFFFFF"
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["Ground"] = "#EEF1F4", ["Panel"] = "#FFFFFF", ["Sunk"] = "#F5F7F9", ["Chrome"] = "#E3E8ED",
        ["Line"] = "#D9DFE5", ["LineStrong"] = "#C3CCD5", ["Ink"] = "#18212B", ["Muted"] = "#5C6773",
        ["Accent"] = "#1F6FD1", ["AccentHover"] = "#185BAD", ["AccentSoft"] = "#E8F0FB",
        ["Good"] = "#1E8A52", ["Warn"] = "#B7791F", ["WarnSoft"] = "#FBF3E6", ["Bad"] = "#C23B32",
        ["HoverOverlay"] = "#18212B"
    };

    private static readonly List<WeakReference<FrameworkElement>> Registered = [];
    private static bool _listening;

    public static bool IsDark { get; private set; } = DetectDark();

    /// <summary>Raised on the UI thread after the palette switches.</summary>
    public static event Action? Changed;

    private static string _accent = Branding.DefaultAccent;

    public static string Hex(string key) => CurrentPalette()[key];

    /// <summary>Changes the brand accent and recolors every registered window.</summary>
    public static void SetAccent(string hex)
    {
        if (string.Equals(_accent, hex, StringComparison.OrdinalIgnoreCase)) return;
        _accent = hex;
        ReapplyAll();
    }

    private static Dictionary<string, string> CurrentPalette()
    {
        var palette = new Dictionary<string, string>(IsDark ? Dark : Light);
        var accent = (Color)ColorConverter.ConvertFromString(_accent);
        var panel = (Color)ColorConverter.ConvertFromString(palette["Panel"]);
        palette["Accent"] = _accent;
        palette["AccentHover"] = ToHex(Mix(accent, IsDark ? Colors.White : Colors.Black, 0.16));
        palette["AccentSoft"] = ToHex(Mix(panel, accent, IsDark ? 0.2 : 0.1));
        return palette;
    }

    private static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static SolidColorBrush Brush(string key)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Hex(key)));
        brush.Freeze();
        return brush;
    }

    /// <summary>Applies the current palette to an element's theme dictionaries and keeps it in sync.</summary>
    public static void Register(FrameworkElement element)
    {
        Apply(element.Resources);
        lock (Registered) Registered.Add(new WeakReference<FrameworkElement>(element));
        if (_listening) return;
        _listening = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)) return;
            Application.Current?.Dispatcher.BeginInvoke(Refresh);
        };
    }

    private static void Refresh()
    {
        var dark = DetectDark();
        if (dark == IsDark) return;
        IsDark = dark;
        ReapplyAll();
    }

    private static void ReapplyAll()
    {
        List<FrameworkElement> alive;
        lock (Registered)
        {
            alive = Registered.Select(reference => reference.TryGetTarget(out var target) ? target : null).OfType<FrameworkElement>().ToList();
            Registered.RemoveAll(reference => !reference.TryGetTarget(out _));
        }
        foreach (var element in alive)
        {
            Apply(element.Resources);
            if (element is Window window) ThemeWindows.TintTitleBar(window);
        }
        Changed?.Invoke();
    }

    private static void Apply(ResourceDictionary dictionary)
    {
        var palette = CurrentPalette();
        foreach (var (key, hex) in palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            SetBrush(dictionary, key, color);
        }
        SetColor(dictionary, "GroundColor", (Color)ColorConverter.ConvertFromString(palette["Ground"]));
        SetColor(dictionary, "AccentColor", (Color)ColorConverter.ConvertFromString(palette["Accent"]));
    }

    private static void SetBrush(ResourceDictionary dictionary, string key, Color color)
    {
        foreach (var owner in DictionariesContaining(dictionary, key))
        {
            if (owner[key] is SolidColorBrush { IsFrozen: false } brush) brush.Color = color;
            else owner[key] = new SolidColorBrush(color);
        }
    }

    private static void SetColor(ResourceDictionary dictionary, string key, Color color)
    {
        foreach (var owner in DictionariesContaining(dictionary, key)) owner[key] = color;
    }

    private static IEnumerable<ResourceDictionary> DictionariesContaining(ResourceDictionary dictionary, string key)
    {
        if (dictionary.Contains(key)) yield return dictionary;
        foreach (var merged in dictionary.MergedDictionaries)
            foreach (var owner in DictionariesContaining(merged, key))
                yield return owner;
    }

    private static bool DetectDark()
    {
        var forced = Environment.GetEnvironmentVariable("CALLBRIDGE_THEME");
        if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
