using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CallBridge.Desktop;

public sealed record TraySnapshot(
    TrayPhoneStatus Status,
    string StatusText,
    string Extension,
    string Headset,
    string? ActiveCaller,
    string? Voicemail = null,
    string? AvailableUpdate = null);

/// <summary>
/// Small pop-up shown from the notification-area icon. It closes when it loses focus.
/// </summary>
public sealed class TrayFlyout : Window
{
    private const double FlyoutWidth = 280;
    private static Brush Surface => ThemePalette.Brush("Panel");
    private static Brush Border => ThemePalette.Brush("Line");
    private static Brush Ink => ThemePalette.Brush("Ink");
    private static Brush Muted => ThemePalette.Brush("Muted");
    private static Brush Accent => ThemePalette.Brush("Accent");
    private bool _closing;

    public TrayFlyout(TraySnapshot snapshot, Action openWindow, Action openSettings, Action quit, Action? checkForUpdates = null)
    {
        ThemeWindows.MergeTheme(this);
        FontFamily = (FontFamily)FindResource("UiFont");
        UseLayoutRounding = true;
        Title = Branding.ProductName;
        Width = FlyoutWidth;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = Border;
        Padding = new Thickness(1);

        var root = new StackPanel { Background = Surface };
        root.Children.Add(BuildHeader(snapshot));
        root.Children.Add(Row("Extension", string.IsNullOrWhiteSpace(snapshot.Extension) ? "Not set up" : snapshot.Extension));
        root.Children.Add(Row("Headset", snapshot.Headset));
        if (!string.IsNullOrWhiteSpace(snapshot.Voicemail))
            root.Children.Add(Row("Voicemail", snapshot.Voicemail));
        if (!string.IsNullOrWhiteSpace(snapshot.ActiveCaller))
            root.Children.Add(Row("On call with", snapshot.ActiveCaller));

        var actions = new Grid { Margin = new Thickness(14, 10, 14, 8) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        var settings = ActionButton("Settings", primary: false, () => Run(openSettings));
        var open = ActionButton("Open CallBridge", primary: true, () => Run(openWindow));
        Grid.SetColumn(open, 2);
        actions.Children.Add(settings);
        actions.Children.Add(open);
        root.Children.Add(actions);

        var footer = new DockPanel { Margin = new Thickness(14, 0, 14, 14), LastChildFill = false };
        if (checkForUpdates is not null)
        {
            var updates = ActionButton(snapshot.AvailableUpdate is null ? "Check for updates" : $"Install update {snapshot.AvailableUpdate}", primary: false, () => Run(checkForUpdates));
            updates.Background = Surface;
            updates.BorderThickness = new Thickness(0);
            updates.Foreground = snapshot.AvailableUpdate is null ? Muted : Accent;
            AutomationProperties.SetName(updates, snapshot.AvailableUpdate is null ? "Check for updates" : "Install update");
            DockPanel.SetDock(updates, Dock.Left);
            footer.Children.Add(updates);
        }
        var quitButton = ActionButton("Quit CallBridge", primary: false, () => Run(quit));
        quitButton.Background = Surface;
        quitButton.BorderThickness = new Thickness(0);
        quitButton.Foreground = Muted;
        DockPanel.SetDock(quitButton, Dock.Right);
        footer.Children.Add(quitButton);
        root.Children.Add(footer);

        Content = root;
        AutomationProperties.SetName(this, "CallBridge status");
        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            e.Handled = true;
            SafeClose();
        };
        Loaded += (_, _) => PositionNearTray();
        ContentRendered += (_, _) => open.Focus();
    }

    private void Run(Action action)
    {
        SafeClose();
        action();
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void PositionNearTray()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
    }

    private static FrameworkElement BuildHeader(TraySnapshot snapshot)
    {
        var header = new DockPanel { Margin = new Thickness(14, 14, 14, 8), LastChildFill = false };
        var name = new TextBlock { Text = Branding.ProductName, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ink, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 };
        DockPanel.SetDock(name, Dock.Left);
        header.Children.Add(name);

        var status = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 6, 0),
            Fill = StatusBrush(snapshot.Status),
            VerticalAlignment = VerticalAlignment.Center
        });
        status.Children.Add(new TextBlock { Text = snapshot.StatusText, Foreground = Muted, FontSize = 12 });
        DockPanel.SetDock(status, Dock.Right);
        header.Children.Add(status);
        return header;
    }

    private static FrameworkElement Row(string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(14, 4, 14, 4) };
        var labelText = new TextBlock { Text = label, Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(labelText, Dock.Left);
        row.Children.Add(labelText);
        row.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = Ink,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 0, 0),
            ToolTip = value
        });
        return row;
    }

    private static Button ActionButton(string text, bool primary, Action click)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 32,
            Padding = new Thickness(10, 4, 10, 4),
            Background = primary ? Accent : Surface,
            Foreground = primary ? Brushes.White : Ink,
            BorderBrush = primary ? Accent : Border,
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Brush StatusBrush(TrayPhoneStatus status) => status switch
    {
        TrayPhoneStatus.Registered or TrayPhoneStatus.OnCall => ThemePalette.Brush("Good"),
        TrayPhoneStatus.Connecting => ThemePalette.Brush("Warn"),
        _ => ThemePalette.Brush("Muted")
    };
}
