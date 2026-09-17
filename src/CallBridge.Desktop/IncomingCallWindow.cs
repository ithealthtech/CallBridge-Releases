using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace CallBridge.Desktop;

public sealed record IncomingCallContext(
    string? ContactName,
    string? CompanyName,
    string? CompanyId,
    IReadOnlyList<ConnectWiseTicketSummary> Tickets,
    string? Message,
    string? LastCall = null);

/// <summary>
/// Compact screen-pop window shown in front of other apps when a call rings.
/// It shows the caller's ConnectWise contact, company, and open tickets.
/// </summary>
public sealed class IncomingCallWindow : Window
{
    public const double PopWidth = 400;

    private readonly Func<Task> _answer;
    private readonly Func<Task> _decline;
    private readonly Action<string> _openTicket;
    private readonly TextBlock _initials;
    private readonly TextBlock _name;
    private readonly TextBlock _company;
    private readonly TextBlock _number;
    private readonly Ellipse _avatarRing;
    private readonly StackPanel _context;
    private readonly Button _answerButton;
    private readonly Button _declineButton;
    private readonly string _callerLabel;
    private bool _acting;

    public IncomingCallWindow(string callerLabel, string number, Func<Task> answer, Func<Task> decline, Action<string> openTicket)
    {
        _answer = answer;
        _decline = decline;
        _openTicket = openTicket;
        _callerLabel = string.IsNullOrWhiteSpace(callerLabel) ? "Unknown caller" : callerLabel.Trim();
        ThemeWindows.MergeTheme(this);

        Title = "Incoming call";
        Width = PopWidth;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Topmost = true;
        Background = Themed("Line");
        Padding = new Thickness(1);
        FontFamily = (FontFamily)FindResource("UiFont");
        FontSize = 13;
        Foreground = Themed("Ink");
        UseLayoutRounding = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AutomationProperties.SetName(this, "Incoming call");

        var root = new StackPanel { Background = Themed("Panel") };

        var titleBar = new DockPanel { Background = Themed("Chrome"), Height = 34, LastChildFill = true };
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var close = ChromeButton("", "Hide this pop-up. The call keeps ringing in CallBridge.");
        close.Click += (_, _) => Hide();
        var minimize = ChromeButton("", "Minimize");
        minimize.Click += (_, _) => WindowState = WindowState.Minimized;
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(minimize, Dock.Right);
        titleBar.Children.Add(close);
        titleBar.Children.Add(minimize);
        var logo = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Background = Branding.Logo is null ? Themed("Accent") : Brushes.Transparent,
            Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = Branding.Logo is { } popLogo
                ? new Image { Source = popLogo, Stretch = Stretch.Uniform }
                : new TextBlock { Text = Branding.ProductInitials, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        DockPanel.SetDock(logo, Dock.Left);
        titleBar.Children.Add(logo);
        titleBar.Children.Add(new TextBlock { Text = "Incoming call", FontSize = 12, Foreground = Themed("Muted"), VerticalAlignment = VerticalAlignment.Center });
        root.Children.Add(titleBar);

        var ring = new StackPanel { Margin = new Thickness(16) };
        var caller = new DockPanel();
        _initials = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Themed("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _avatarRing = new Ellipse { Stroke = Themed("Accent"), StrokeThickness = 3, Opacity = 0, Margin = new Thickness(-4) };
        var avatar = new Grid { Width = 44, Height = 44, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
        avatar.Children.Add(_avatarRing);
        avatar.Children.Add(new Ellipse { Fill = Themed("Sunk"), Stroke = Themed("Line"), StrokeThickness = 1 });
        avatar.Children.Add(_initials);
        DockPanel.SetDock(avatar, Dock.Left);
        caller.Children.Add(avatar);
        var identity = new StackPanel();
        _name = new TextBlock { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Themed("Ink"), TextTrimming = TextTrimming.CharacterEllipsis };
        _company = new TextBlock { Foreground = Themed("Muted"), TextTrimming = TextTrimming.CharacterEllipsis };
        _number = new TextBlock { Foreground = Themed("Muted"), FontSize = 12, FontFamily = (FontFamily)FindResource("MonoFont"), Text = number };
        identity.Children.Add(_name);
        identity.Children.Add(_company);
        identity.Children.Add(_number);
        caller.Children.Add(identity);
        ring.Children.Add(caller);

        var actions = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        actions.ColumnDefinitions.Add(new ColumnDefinition());
        _declineButton = new Button { Content = "Decline", Style = (Style)FindResource("DangerButton"), MinHeight = 38 };
        _answerButton = new Button { Content = "Answer", Style = (Style)FindResource("GoodButton"), MinHeight = 38 };
        AutomationProperties.SetName(_declineButton, "Decline incoming call");
        AutomationProperties.SetName(_answerButton, "Answer incoming call");
        _declineButton.Click += async (_, _) => await RunAsync(_decline);
        _answerButton.Click += async (_, _) => await RunAsync(_answer);
        Grid.SetColumn(_answerButton, 2);
        actions.Children.Add(_declineButton);
        actions.Children.Add(_answerButton);
        ring.Children.Add(actions);
        root.Children.Add(ring);

        root.Children.Add(new Border { Height = 1, Background = Themed("Line") });
        _context = new StackPanel { Background = Themed("Sunk") };
        root.Children.Add(_context);

        Content = root;
        ShowIdentity(null, null);
        SetContext(null);

        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                await RunAsync(_decline);
            }
        };
        Loaded += (_, _) =>
        {
            PositionOnScreen();
            StartPulse();
        };
        ContentRendered += (_, _) => _answerButton.Focus();
    }

    /// <summary>Replaces the lookup area. Pass null to show the loading state.</summary>
    public void SetContext(IncomingCallContext? context)
    {
        _context.Children.Clear();
        _context.Children.Add(Label("OPEN TICKETS · CONNECTWISE"));

        if (context is null)
        {
            _context.Children.Add(Note("Looking up the caller in ConnectWise…", bottom: 14));
            return;
        }

        ShowIdentity(context.ContactName, context.CompanyName);
        if (context.Tickets.Count == 0)
        {
            _context.Children.Add(Note(context.Message ?? "No open tickets for this company.", bottom: string.IsNullOrWhiteSpace(context.LastCall) ? 14 : 4));
        }
        else
        {
            var list = new StackPanel { Margin = new Thickness(16, 0, 16, 4) };
            for (var index = 0; index < context.Tickets.Count; index++)
                list.Children.Add(TicketRow(context.Tickets[index], index > 0));
            _context.Children.Add(list);
            if (!string.IsNullOrWhiteSpace(context.Message)) _context.Children.Add(Note(context.Message, bottom: 4));
        }
        if (!string.IsNullOrWhiteSpace(context.LastCall)) _context.Children.Add(Note(context.LastCall, bottom: 14));
    }

    public void Bring()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        Activate();
    }

    private void StartPulse()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var pulse = new DoubleAnimation(0, 0.7, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        _avatarRing.BeginAnimation(OpacityProperty, pulse);
    }

    private void ShowIdentity(string? contactName, string? companyName)
    {
        var name = string.IsNullOrWhiteSpace(contactName) ? _callerLabel : contactName.Trim();
        _name.Text = name;
        _name.ToolTip = name;
        _initials.Text = Initials(name);
        _company.Text = string.IsNullOrWhiteSpace(companyName) ? "Not matched to a ConnectWise company" : companyName.Trim();
        _company.ToolTip = _company.Text;
        _number.Visibility = string.IsNullOrWhiteSpace(_number.Text) || _number.Text == name ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_acting) return;
        _acting = true;
        _answerButton.IsEnabled = false;
        _declineButton.IsEnabled = false;
        try { await action(); }
        finally
        {
            _acting = false;
            if (IsLoaded)
            {
                _answerButton.IsEnabled = true;
                _declineButton.IsEnabled = true;
            }
        }
    }

    private void PositionOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 24;
        Top = area.Top + 24;
    }

    private FrameworkElement TicketRow(ConnectWiseTicketSummary ticket, bool divider)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var number = new TextBlock { Text = $"#{ticket.Id}", Foreground = Themed("Muted"), FontSize = 12, FontFamily = (FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var summary = new TextBlock { Text = string.IsNullOrWhiteSpace(ticket.Summary) ? "(no summary)" : ticket.Summary, Foreground = Themed("Ink"), FontWeight = FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = ticket.Summary };
        var pill = Pill(ShortPriority(ticket), PriorityBrush(ticket.Priority));
        Grid.SetColumn(summary, 1);
        Grid.SetColumn(pill, 2);
        row.Children.Add(number);
        row.Children.Add(summary);
        row.Children.Add(pill);

        var button = new Button
        {
            Content = row,
            Style = (Style)FindResource("GhostButton"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(4, 7, 4, 7),
            MinHeight = 0,
            ToolTip = $"Open ticket #{ticket.Id} in ConnectWise"
        };
        AutomationProperties.SetName(button, $"Ticket {ticket.Id}: {ticket.Summary}");
        button.Click += (_, _) => _openTicket(ticket.Id);
        return new Border { BorderBrush = Themed("Line"), BorderThickness = new Thickness(0, divider ? 1 : 0, 0, 0), Child = button };
    }

    private Button ChromeButton(string glyph, string tooltip)
    {
        var button = new Button
        {
            Content = glyph,
            Style = (Style)FindResource("IconButton"),
            Width = 40,
            MinHeight = 34,
            FontSize = 10,
            ToolTip = tooltip
        };
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static string ShortPriority(ConnectWiseTicketSummary ticket)
    {
        var priority = ticket.Priority ?? "";
        var match = System.Text.RegularExpressions.Regex.Match(priority, @"Priority\s*(\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return $"P{match.Groups[1].Value}";
        return string.IsNullOrWhiteSpace(ticket.Status) ? "Open" : ticket.Status;
    }

    internal static Brush PriorityBrush(string? priority)
    {
        var text = priority ?? "";
        if (text.Contains('1') || text.Contains('2') || text.Contains("critical", StringComparison.OrdinalIgnoreCase) || text.Contains("high", StringComparison.OrdinalIgnoreCase)) return ThemePalette.Brush("Bad");
        if (text.Contains('3') || text.Contains("medium", StringComparison.OrdinalIgnoreCase)) return ThemePalette.Brush("Warn");
        return ThemePalette.Brush("Muted");
    }

    internal static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => char.IsLetter(part[0]))
            .ToArray();
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => $"{parts[0][0]}{parts[^1][0]}".ToUpperInvariant()
        };
    }

    private Brush Themed(string key) => (Brush)FindResource(key);

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Themed("Muted"),
        Margin = new Thickness(16, 12, 16, 6)
    };

    private TextBlock Note(string text, double bottom) => new()
    {
        Text = text,
        Foreground = Themed("Muted"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(16, 0, 16, bottom)
    };

    internal static Border Pill(string text, Brush brush) => new()
    {
        BorderBrush = brush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 1, 7, 1),
        Margin = new Thickness(10, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Medium, Foreground = brush }
    };

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}

internal static class UiPalette
{
    public static Brush Brush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
