using System.Runtime.InteropServices;

namespace CallBridge.Desktop;

public enum TrayPhoneStatus
{
    Offline,
    Connecting,
    Registered,
    OnCall
}

public enum TrayIconAction
{
    None,
    OpenFlyout,
    ActivateWindow
}

/// <summary>
/// Persistent notification-area icon. It shows phone status at rest and raises the
/// incoming-call notification and taskbar attention while a call rings.
/// </summary>
public sealed class WindowsIncomingCallNotifier : IDisposable
{
    public const int CallbackMessage = 0x8001;
    public static readonly int TaskbarCreatedMessage = unchecked((int)RegisterWindowMessage("TaskbarCreated"));
    private const uint IconId = 1;
    private const uint NotifyAdd = 0x00000000;
    private const uint NotifyModify = 0x00000001;
    private const uint NotifyDelete = 0x00000002;
    private const uint NotifyMessage = 0x00000001;
    private const uint NotifyIcon = 0x00000002;
    private const uint NotifyTip = 0x00000004;
    private const uint NotifyInfo = 0x00000010;
    private const uint InfoFlag = 0x00000001;
    private const int LeftButtonUp = 0x0202;
    private const int RightButtonUp = 0x0205;
    private const int BalloonUserClick = 0x0405;
    private const uint FlashStop = 0;
    private const uint FlashAll = 3;
    private const uint FlashUntilForeground = 12;
    private readonly IntPtr _windowHandle;
    private readonly Dictionary<TrayPhoneStatus, IntPtr> _icons = [];
    private NotifyIconData _data;
    private bool _added;
    private bool _disposed;

    public WindowsIncomingCallNotifier(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _data = CreateData(windowHandle);
    }

    public TrayPhoneStatus Status { get; private set; } = TrayPhoneStatus.Offline;

    public void SetStatus(TrayPhoneStatus status, string tooltip)
    {
        if (_disposed) return;
        Status = status;
        _data.hIcon = GetIcon(status);
        _data.szTip = SafeText(tooltip, "CallBridge", 120);
        _data.uFlags = NotifyMessage | NotifyIcon | NotifyTip;
        Apply();
    }

    public void Show(string? caller)
    {
        if (_disposed) return;
        _data.szInfoTitle = "Incoming call";
        _data.szInfo = SafeText(caller, "Unknown caller", 220);
        _data.dwInfoFlags = InfoFlag;
        _data.uFlags = NotifyMessage | NotifyIcon | NotifyTip | NotifyInfo;
        Apply();
        Flash(true);
    }

    public void ShowInformation(string title, string message)
    {
        if (_disposed) return;
        _data.szInfoTitle = SafeText(title, "CallBridge", 60);
        _data.szInfo = SafeText(message, "", 220);
        _data.dwInfoFlags = InfoFlag;
        _data.uFlags = NotifyMessage | NotifyIcon | NotifyTip | NotifyInfo;
        Apply();
    }

    public void Clear()
    {
        Flash(false);
        if (!_added || _disposed) return;
        _data.szInfo = "";
        _data.szInfoTitle = "";
        _data.uFlags = NotifyInfo;
        Shell_NotifyIcon(NotifyModify, ref _data);
    }

    /// <summary>Redraws the icon, for example after the brand accent changes.</summary>
    public void ResetIcons()
    {
        if (_disposed) return;
        var old = _icons.Values.ToList();
        _icons.Clear();
        SetStatus(Status, _data.szTip);
        foreach (var icon in old) DestroyIcon(icon);
    }

    /// <summary>Re-adds the icon after Explorer restarts.</summary>
    public void Restore()
    {
        _added = false;
        SetStatus(Status, _data.szTip);
    }

    public static TrayIconAction ClassifyMessage(IntPtr messageData)
    {
        var message = unchecked((int)messageData.ToInt64());
        return message switch
        {
            LeftButtonUp or RightButtonUp => TrayIconAction.OpenFlyout,
            BalloonUserClick => TrayIconAction.ActivateWindow,
            _ => TrayIconAction.None
        };
    }

    private void Apply()
    {
        if (!_added)
        {
            _data.uFlags |= NotifyMessage | NotifyIcon | NotifyTip;
            _added = Shell_NotifyIcon(NotifyAdd, ref _data);
        }
        else
        {
            Shell_NotifyIcon(NotifyModify, ref _data);
        }
    }

    private void Flash(bool start)
    {
        var info = new FlashWindowInfo
        {
            cbSize = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            hwnd = _windowHandle,
            dwFlags = start ? FlashAll | FlashUntilForeground : FlashStop,
            uCount = start ? uint.MaxValue : 0,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    private IntPtr GetIcon(TrayPhoneStatus status)
    {
        if (_icons.TryGetValue(status, out var existing)) return existing;
        var dot = status switch
        {
            TrayPhoneStatus.Registered => 0xFF1E8A52u,
            TrayPhoneStatus.OnCall => 0xFF1E8A52u,
            TrayPhoneStatus.Connecting => 0xFFE0A443u,
            _ => 0xFF8A949Eu
        };
        var accent = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(ThemePalette.Hex("Accent"));
        var tile = 0xFF000000u | ((uint)accent.R << 16) | ((uint)accent.G << 8) | accent.B;
        var icon = TrayIconRenderer.Create(tile, dot, status == TrayPhoneStatus.OnCall);
        if (icon == IntPtr.Zero) icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        else _icons[status] = icon;
        return icon;
    }

    private static NotifyIconData CreateData(IntPtr windowHandle) => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = windowHandle,
        uID = IconId,
        uFlags = NotifyMessage | NotifyIcon | NotifyTip,
        uCallbackMessage = CallbackMessage,
        hIcon = IntPtr.Zero,
        szTip = "CallBridge",
        szInfo = "",
        szInfoTitle = ""
    };

    private static string SafeText(string? value, string fallback, int maximumLength)
    {
        var safe = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safe.Length > maximumLength) safe = safe[..maximumLength];
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flash(false);
        if (_added) Shell_NotifyIcon(NotifyDelete, ref _data);
        _added = false;
        _disposed = true;
        foreach (var icon in _icons.Values) DestroyIcon(icon);
        _icons.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);
}

/// <summary>Draws the 32x32 tray icon: an accent tile with a status dot.</summary>
internal static class TrayIconRenderer
{
    private const int Size = 32;

    public static uint[] RenderPixels(uint tile, uint dot, bool ring)
    {
        var pixels = new uint[Size * Size];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var px = x + 0.5;
            var py = y + 0.5;
            uint color = 0;
            if (Coverage(RoundedRectDistance(px, py, 2, 2, 28, 28, 6)) is var tileCover and > 0)
                color = WithAlpha(tile, tileCover);
            // White handset glyph approximated as a thick arc in the tile.
            var arc = Math.Abs(Math.Sqrt((px - 14) * (px - 14) + (py - 14) * (py - 14)) - 7.5) - 2;
            if (px < 17.5 && py < 17.5 && Coverage(arc) is var arcCover and > 0 && color != 0)
                color = Blend(color, 0xFFFFFFFFu, arcCover);
            var dotDistance = Math.Sqrt((px - 24) * (px - 24) + (py - 24) * (py - 24));
            if (dotDistance < 8.5) color = dotDistance < 7.5 ? 0 : WithAlpha(color, Math.Clamp(dotDistance - 7.5, 0, 1));
            if (Coverage(dotDistance - 6) is var dotCover and > 0) color = Blend(color, dot, dotCover);
            if (ring && Coverage(Math.Abs(dotDistance - 2.5) - 1) is var ringCover and > 0 && dotDistance < 6)
                color = Blend(color, 0xFFFFFFFFu, ringCover);
            pixels[y * Size + x] = color;
        }
        return pixels;
    }

    public static IntPtr Create(uint tile, uint dot, bool ring)
    {
        var pixels = RenderPixels(tile, dot, ring);
        var header = new BitmapInfoHeader
        {
            biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            biWidth = Size,
            biHeight = -Size,
            biPlanes = 1,
            biBitCount = 32
        };
        var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        var mask = CreateBitmap(Size, Size, 1, 1, IntPtr.Zero);
        try
        {
            Marshal.Copy((int[])(object)pixels, 0, bits, pixels.Length);
            var info = new IconInfo { fIcon = true, hbmColor = color, hbmMask = mask };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            if (mask != IntPtr.Zero) DeleteObject(mask);
        }
    }

    private static double RoundedRectDistance(double px, double py, double left, double top, double width, double height, double radius)
    {
        var cx = left + width / 2;
        var cy = top + height / 2;
        var qx = Math.Abs(px - cx) - (width / 2 - radius);
        var qy = Math.Abs(py - cy) - (height / 2 - radius);
        return Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - radius;
    }

    private static double Coverage(double signedDistance) => Math.Clamp(0.5 - signedDistance, 0, 1);

    private static uint WithAlpha(uint color, double coverage) =>
        ((uint)Math.Round(((color >> 24) & 0xFF) * coverage) << 24) | (color & 0x00FFFFFF);

    private static uint Blend(uint under, uint over, double coverage)
    {
        var overAlpha = ((over >> 24) & 0xFF) / 255.0 * coverage;
        var underAlpha = ((under >> 24) & 0xFF) / 255.0;
        var alpha = overAlpha + underAlpha * (1 - overAlpha);
        if (alpha <= 0) return 0;
        uint Channel(int shift) =>
            (uint)Math.Round((((over >> shift) & 0xFF) * overAlpha + ((under >> shift) & 0xFF) * underAlpha * (1 - overAlpha)) / alpha);
        return ((uint)Math.Round(alpha * 255) << 24) | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);
}
