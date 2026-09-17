using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CallBridge.Desktop;

/// <summary>Applies the shared CallBridge theme to windows that are built in code.</summary>
internal static class ThemeWindows
{
    private static readonly Uri ThemeUri = new("/CallBridge.Desktop;component/Themes/CallBridgeTheme.xaml", UriKind.Relative);
    private const int DwmCaptionColor = 35;
    private const int DwmBorderColor = 34;
    private const int DwmTextColor = 36;
    private const int DwmUseImmersiveDarkMode = 20;

    public static void MergeTheme(FrameworkElement element)
    {
        if (element.Resources.MergedDictionaries.Any(dictionary => dictionary.Source == ThemeUri)) return;
        element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeUri });
        ThemePalette.Register(element);
    }

    /// <summary>Themes a code-built dialog: dark ground, light text, shared styles, and a dark title bar.</summary>
    public static void ApplyDialog(Window window)
    {
        MergeTheme(window);
        window.Background = (System.Windows.Media.Brush)window.FindResource("Ground");
        window.Foreground = (System.Windows.Media.Brush)window.FindResource("Ink");
        window.FontFamily = (System.Windows.Media.FontFamily)window.FindResource("UiFont");
        window.SourceInitialized += (_, _) => TintTitleBar(window);
    }

    /// <summary>Gives the native title bar the dark mockup chrome color. Older Windows ignores the colors but keeps dark mode.</summary>
    public static void TintTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var enabled = ThemePalette.IsDark ? 1 : 0;
        try { DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        SetColor(handle, DwmCaptionColor, ThemePalette.Hex("Chrome"));
        SetColor(handle, DwmBorderColor, ThemePalette.Hex("Line"));
        SetColor(handle, DwmTextColor, ThemePalette.Hex("Ink"));
    }

    private static void SetColor(IntPtr handle, int attribute, string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var colorRef = color.R | (color.G << 8) | (color.B << 16);
        try { DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
