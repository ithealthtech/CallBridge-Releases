using Microsoft.Win32;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CallBridge.Desktop;

public partial class MainWindow : Window
{
    private const string Version = "0.6.0";
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private AppSettings _settings = new();
    private string? _activeCallId;
    private bool _registered;
    private bool _muted;
    private bool _held;
    private List<RowItem> _currentRows = [];

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        WireEvents();
        ApplyBranding();
        RenderPhone();
        _ = RefreshHealthAsync();
    }

    private void WireEvents()
    {
        ProviderBox.Items.Add("Mock provider");
        ProviderBox.Items.Add("Standard SIP test");
        ProviderBox.Items.Add("Axion / Noixa pending");
        ProviderBox.SelectedItem = _settings.Provider;
        ProviderBox.SelectionChanged += (_, _) =>
        {
            _settings.Provider = ProviderBox.SelectedItem?.ToString() ?? "Mock provider";
            SaveSettings(false);
            UpdateProviderStatus();
        };

        RegisterButton.Click += async (_, _) => await ToggleRegistrationAsync();
        CallButton.Click += async (_, _) => await ToggleCallAsync();
        MuteButton.Click += async (_, _) => await ToggleMuteAsync();
        HoldButton.Click += async (_, _) => await ToggleHoldAsync();
        TransferButton.Click += async (_, _) => await TransferAsync();
        DtmfButton.Click += async (_, _) => await SendDtmfAsync();
        SearchBox.TextChanged += (_, _) => RenderRows(FilterRows(SearchBox.Text));

        DashboardButton.Click += (_, _) => ShowRows("Dashboard", "Connector health, registration, and operational status", DashboardRows());
        PhoneButton.Click += (_, _) => RenderPhone();
        ContactsButton.Click += (_, _) => ShowRows("Contacts", "People, queues, and extensions", ContactRows());
        HistoryButton.Click += async (_, _) => ShowRows("Call history", "Inbound, outbound, and missed calls", await HistoryRowsAsync());
        MessagesButton.Click += (_, _) => ShowRows("Messages", "SMS and internal chat", MessageRows());
        VoicemailButton.Click += (_, _) => ShowRows("Voicemail", "New and saved voice messages", VoicemailRows());
        ParkingButton.Click += (_, _) => ShowRows("Parking", "Parked calls and pickup slots", ParkingRows());
        RecordingsButton.Click += (_, _) => ShowRows("Recordings", "Call recordings and review queue", RecordingRows());
        MspButton.Click += (_, _) => ShowRows("MSP actions", "Support workflows tied to phone activity", MspRows());
        SettingsButton.Click += (_, _) => ShowSettings();

        foreach (Button button in DialPad.Children.OfType<Button>())
        {
            button.Click += async (_, _) =>
            {
                var digit = button.Content.ToString()![0].ToString();
                if (_activeCallId is null) DialText.Text += digit;
                else await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/dtmf", new { digits = digit });
            };
        }
    }

    private void LoadSettings()
    {
        if (File.Exists(_settingsPath))
        {
            var json = File.ReadAllText(_settingsPath);
            _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions()) ?? new AppSettings();
        }
        else
        {
            SaveSettings(false);
        }
    }

    private void SaveSettings(bool apply)
    {
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, JsonOptions()));
        if (apply) ApplyBranding();
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void ApplyBranding()
    {
        Title = $"{_settings.ProductName} - {_settings.CompanyName}";
        BrandTitle.Text = _settings.ProductName.ToUpperInvariant();
        BrandSubtitle.Text = _settings.CompanyName.ToUpperInvariant();
        LogoText.Text = new string(_settings.ProductName.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
        ExtensionText.Text = $"Extension {_settings.Extension}";
        ProviderBox.SelectedItem = _settings.Provider;
        UpdateProviderStatus();
    }

    private void RenderPhone()
    {
        PageTitle.Text = "Phone";
        PhoneView.Visibility = Visibility.Visible;
        ListView.Visibility = Visibility.Collapsed;
        PanelHeading.Text = "Favorites";
        MainList.ItemsSource = ContactRows().Take(5).ToList();
    }

    private void ShowRows(string title, string subtitle, List<RowItem> rows)
    {
        PageTitle.Text = title;
        ListHeading.Text = title;
        ListSubheading.Text = subtitle;
        SearchBox.Text = "";
        _currentRows = rows;
        PhoneView.Visibility = Visibility.Collapsed;
        ListView.Visibility = Visibility.Visible;
        RenderRows(rows);
    }

    private void RenderRows(IEnumerable<RowItem> rows)
    {
        RowsList.ItemsSource = rows.ToList();
    }

    private IEnumerable<RowItem> FilterRows(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _currentRows;
        return _currentRows.Where(r => $"{r.Title} {r.Detail} {r.Action}".Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RefreshHealthAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"{_settings.ApiBase.TrimEnd('/')}/health");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(response.StatusCode.ToString());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : "online";
            ApiMetric.Text = version ?? "Online";
            StatusText.Text = "Connector online";
            StatusDot.Fill = Brush("#42B7A8");
        }
        catch
        {
            ApiMetric.Text = "Offline";
            StatusText.Text = "Connector offline";
            StatusDot.Fill = Brush("#DFAE4B");
        }
    }

    private async Task ToggleRegistrationAsync()
    {
        if (_registered)
        {
            _registered = false;
            UpdateProviderStatus();
            return;
        }

        if (_settings.Provider == "Axion / Noixa pending")
        {
            MessageBox.Show("Axion/Noixa calling requires their approved SBC/WebRTC integration details. This provider is intentionally blocked until those are supplied.", "CallBridge");
            return;
        }

        if (_settings.Provider == "Standard SIP test" && string.IsNullOrWhiteSpace(_settings.SipServer))
        {
            MessageBox.Show("Add a SIP server, username, and password in Settings before registering the Standard SIP provider.", "CallBridge");
            return;
        }

        _registered = true;
        UpdateProviderStatus();
        await Task.CompletedTask;
    }

    private void UpdateProviderStatus()
    {
        RegistrationMetric.Text = _registered ? "Registered" : "Not registered";
        RegisterButton.Content = _registered ? "Unregister" : "Register";
        PresenceText.Text = _registered ? "Available" : "Available";
        PresenceDot.Fill = _registered ? Brush("#42B7A8") : Brush("#DFAE4B");
    }

    private async Task ToggleCallAsync()
    {
        if (_activeCallId is not null)
        {
            await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/hangup", new { });
            EndLocalCall("READY");
            return;
        }

        if (string.IsNullOrWhiteSpace(DialText.Text)) return;
        if (!_registered && _settings.Provider != "Mock provider")
        {
            MessageBox.Show("Register the selected provider before starting a call.", "CallBridge");
            return;
        }

        var apiCall = await PostTelephonyAsync("/telephony/calls", new { destination = DialText.Text, callerId = _settings.Extension });
        _activeCallId = apiCall ?? $"local-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        CallStateText.Text = _settings.Provider == "Mock provider" ? "CONNECTED - LOCAL MOCK" : "CALL STARTED";
        ActiveCallMetric.Text = DialText.Text;
        CallButton.Content = "End call";
        CallButton.Background = Brush("#C95669");
        Topmost = _settings.AlwaysOnTopDuringCalls;
    }

    private void EndLocalCall(string state)
    {
        _activeCallId = null;
        _muted = false;
        _held = false;
        Topmost = false;
        CallStateText.Text = state;
        ActiveCallMetric.Text = "None";
        CallButton.Content = "Call";
        CallButton.Background = Brush("#42B785");
        MuteButton.Content = "Mute";
        HoldButton.Content = "Hold";
    }

    private async Task ToggleMuteAsync()
    {
        if (_activeCallId is null) return;
        _muted = !_muted;
        await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/mute", new { muted = _muted });
        MuteButton.Content = _muted ? "Unmute" : "Mute";
    }

    private async Task ToggleHoldAsync()
    {
        if (_activeCallId is null) return;
        _held = !_held;
        await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/{(_held ? "hold" : "resume")}", new { });
        HoldButton.Content = _held ? "Resume" : "Hold";
        CallStateText.Text = _held ? "CALL ON HOLD" : "CONNECTED";
    }

    private async Task TransferAsync()
    {
        if (_activeCallId is null) return;
        var prompt = new PromptWindow("Transfer call", "Destination extension or phone number");
        if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.Value))
        {
            await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/transfer", new { destination = prompt.Value });
            EndLocalCall($"TRANSFERRED TO {prompt.Value}");
        }
    }

    private async Task SendDtmfAsync()
    {
        if (_activeCallId is null) return;
        var prompt = new PromptWindow("Send DTMF", "Digits");
        if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.Value))
        {
            await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/dtmf", new { digits = prompt.Value });
        }
    }

    private async Task<string?> PostTelephonyAsync(string path, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions());
            using var response = await _http.PostAsync($"{_settings.ApiBase.TrimEnd('/')}{path}", new StringContent(json, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("call", out var call) && call.TryGetProperty("id", out var id)) return id.GetString();
        }
        catch
        {
            ApiMetric.Text = "Offline";
        }
        return null;
    }

    private async Task<List<RowItem>> HistoryRowsAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"{_settings.ApiBase.TrimEnd('/')}/events/recent?limit=30");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("events", out var events))
            {
                return events.EnumerateArray().Select(e => new RowItem(
                    e.TryGetProperty("caller_number", out var n) ? n.GetString() ?? "Unknown caller" : "Unknown caller",
                    $"{Value(e, "direction")} - {Value(e, "state")} - {Value(e, "occurred_at")}",
                    "Call")).ToList();
            }
        }
        catch { }
        return
        [
            new("Simi Valley Dental Group", "Inbound - today 3:42 PM - 04:18", "Call"),
            new("West Coast Manufacturing", "Missed - today 2:16 PM - (818) 555-0192", "Call"),
            new("Northstar Property Management", "Outbound - today 11:08 AM - 12:06", "Call")
        ];
    }

    private static string Value(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "";

    private List<RowItem> DashboardRows() =>
    [
        new("Provider", $"{_settings.Provider} - {(_registered ? "registered" : "not registered")}", "Open"),
        new("Connector API", $"{_settings.ApiBase} - {ApiMetric.Text}", "Test"),
        new("Extension", $"{_settings.Extension} - caller ID {_settings.CallerId}", "Edit"),
        new("Axion status", "Blocked until official SBC/WebRTC integration details are supplied", "Review"),
        new("MSP workflow", "Contact lookup, ticket creation, call notes, and activity logging ready for API wiring", "Open")
    ];

    private static List<RowItem> ContactRows() =>
    [
        new("Maria Jensen", "Available - (805) 555-0147 - Simi Valley Dental Group", "Call"),
        new("Daniel Kim", "In a call - (818) 555-0192 - West Coast Manufacturing", "Call"),
        new("Front Office", "Available - Extension 200", "Call"),
        new("Support Queue", "2 agents available - Extension 600", "Call"),
        new("After-hours escalation", "On call - Extension 700", "Call")
    ];

    private static List<RowItem> MessageRows() =>
    [
        new("Maria Jensen", "Can you call me when available? - 2 min", "Reply"),
        new("Support Team", "Queue coverage updated - 18 min", "Open"),
        new("Daniel Kim", "Thank you for the quick help - 1 hr", "Reply")
    ];

    private static List<RowItem> VoicemailRows() =>
    [
        new("New voicemail", "(805) 555-0122 - today 1:08 PM - 00:42", "Play"),
        new("Saved voicemail", "Simi Valley Dental Group - yesterday - 01:14", "Play")
    ];

    private static List<RowItem> ParkingRows() =>
    [
        new("Park 701", "Empty", "Park"),
        new("Park 702", "Daniel Kim - 01:12", "Pickup"),
        new("Park 703", "Empty", "Park")
    ];

    private static List<RowItem> RecordingRows() =>
    [
        new("Simi Valley Dental Group", "Inbound - 04:18 - review not required", "Open"),
        new("West Coast Manufacturing", "Missed callback - no recording", "Details"),
        new("Northstar Property Management", "Outbound - 12:06 - tagged support", "Open")
    ];

    private static List<RowItem> MspRows() =>
    [
        new("Create ConnectWise ticket", "Use caller, company, call notes, and selected agreement", "Start"),
        new("Log activity", "Attach call outcome to matched company/contact", "Log"),
        new("Open company", "Search mapped ConnectWise company and contacts", "Open"),
        new("Remote support handoff", "Start support workflow from active call context", "Start")
    ];

    private void ShowSettings()
    {
        var settingsWindow = new SettingsWindow(_settings) { Owner = this };
        if (settingsWindow.ShowDialog() == true)
        {
            _settings = settingsWindow.Settings;
            SaveSettings(true);
            _ = RefreshHealthAsync();
        }
    }

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}

public sealed record RowItem(string Title, string Detail, string Action)
{
    public override string ToString() => $"{Title}\n{Detail}    {Action}";
}

public sealed class AppSettings
{
    public string ApiBase { get; set; } = "http://127.0.0.1:8787";
    public string Extension { get; set; } = "201";
    public string CallerId { get; set; } = "201";
    public string Provider { get; set; } = "Mock provider";
    public string SipServer { get; set; } = "";
    public string SipUsername { get; set; } = "";
    public string SipPassword { get; set; } = "";
    public string SipTransport { get; set; } = "TLS";
    public string Microphone { get; set; } = "Windows default communications device";
    public string Speaker { get; set; } = "Windows default communications device";
    public bool LaunchAtStartup { get; set; }
    public bool AlwaysOnTopDuringCalls { get; set; } = true;
    public string ProductName { get; set; } = "CallBridge";
    public string CompanyName { get; set; } = "IT Health Technologies";
}

public sealed class SettingsWindow : Window
{
    private readonly TextBox _api = Field();
    private readonly TextBox _extension = Field();
    private readonly TextBox _callerId = Field();
    private readonly TextBox _product = Field();
    private readonly TextBox _company = Field();
    private readonly TextBox _sipServer = Field();
    private readonly TextBox _sipUser = Field();
    private readonly PasswordBox _sipPassword = new() { Padding = new Thickness(8) };
    private readonly ComboBox _transport = new();
    private readonly ComboBox _provider = new();
    private readonly CheckBox _startup = new() { Content = "Launch when I sign in to Windows" };
    private readonly CheckBox _topmost = new() { Content = "Keep on top during calls" };
    public AppSettings Settings { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        Settings = new AppSettings
        {
            ApiBase = settings.ApiBase,
            Extension = settings.Extension,
            CallerId = settings.CallerId,
            Provider = settings.Provider,
            SipServer = settings.SipServer,
            SipUsername = settings.SipUsername,
            SipPassword = settings.SipPassword,
            SipTransport = settings.SipTransport,
            Microphone = settings.Microphone,
            Speaker = settings.Speaker,
            LaunchAtStartup = settings.LaunchAtStartup,
            AlwaysOnTopDuringCalls = settings.AlwaysOnTopDuringCalls,
            ProductName = settings.ProductName,
            CompanyName = settings.CompanyName
        };

        Title = "Settings";
        Width = 620;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));

        _api.Text = Settings.ApiBase;
        _extension.Text = Settings.Extension;
        _callerId.Text = Settings.CallerId;
        _product.Text = Settings.ProductName;
        _company.Text = Settings.CompanyName;
        _sipServer.Text = Settings.SipServer;
        _sipUser.Text = Settings.SipUsername;
        _sipPassword.Password = Settings.SipPassword;
        foreach (var item in new[] { "Mock provider", "Standard SIP test", "Axion / Noixa pending" }) _provider.Items.Add(item);
        _provider.SelectedItem = Settings.Provider;
        foreach (var item in new[] { "TLS", "TCP", "UDP" }) _transport.Items.Add(item);
        _transport.SelectedItem = Settings.SipTransport;
        _startup.IsChecked = Settings.LaunchAtStartup;
        _topmost.IsChecked = Settings.AlwaysOnTopDuringCalls;

        var root = new ScrollViewer { Content = BuildForm(), Margin = new Thickness(24) };
        Content = root;
    }

    private StackPanel BuildForm()
    {
        var stack = new StackPanel();
        stack.Children.Add(Label("Account"));
        Add(stack, "Provider", _provider);
        Add(stack, "CallBridge API address", _api);
        Add(stack, "Extension", _extension);
        Add(stack, "Outbound caller ID", _callerId);
        stack.Children.Add(Label("SIP test provider"));
        Add(stack, "SIP server / domain", _sipServer);
        Add(stack, "SIP username", _sipUser);
        Add(stack, "SIP password", _sipPassword);
        Add(stack, "SIP transport", _transport);
        stack.Children.Add(Label("White label"));
        Add(stack, "Product name", _product);
        Add(stack, "Company name", _company);
        stack.Children.Add(_startup);
        stack.Children.Add(_topmost);

        var save = new Button { Content = "Save settings", Margin = new Thickness(0, 18, 0, 0), Padding = new Thickness(14), Background = new SolidColorBrush(Color.FromRgb(17, 136, 182)), Foreground = Brushes.White };
        save.Click += (_, _) =>
        {
            Settings.ApiBase = _api.Text.TrimEnd('/');
            Settings.Extension = _extension.Text;
            Settings.CallerId = _callerId.Text;
            Settings.Provider = _provider.SelectedItem?.ToString() ?? "Mock provider";
            Settings.SipServer = _sipServer.Text;
            Settings.SipUsername = _sipUser.Text;
            Settings.SipPassword = _sipPassword.Password;
            Settings.SipTransport = _transport.SelectedItem?.ToString() ?? "TLS";
            Settings.ProductName = _product.Text;
            Settings.CompanyName = _company.Text;
            Settings.LaunchAtStartup = _startup.IsChecked == true;
            Settings.AlwaysOnTopDuringCalls = _topmost.IsChecked == true;
            SetStartup(Settings.LaunchAtStartup);
            DialogResult = true;
        };
        stack.Children.Add(save);
        return stack;
    }

    private static TextBlock Label(string text) => new() { Text = text.ToUpperInvariant(), Margin = new Thickness(0, 16, 0, 8), FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(17, 136, 182)) };

    private static TextBox Field() => new() { Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8) };

    private static void Add(Panel parent, string label, Control control)
    {
        parent.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 4, 0, 5), Foreground = new SolidColorBrush(Color.FromRgb(102, 117, 134)) });
        control.Margin = new Thickness(0, 0, 0, 8);
        parent.Children.Add(control);
    }

    private static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key is null) return;
        if (enabled) key.SetValue("CallBridgeDesktop", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("CallBridgeDesktop", false);
    }
}

public sealed class PromptWindow : Window
{
    private readonly TextBox _value = new() { Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(8) };
    public string Value => _value.Text;

    public PromptWindow(string title, string label)
    {
        Title = title;
        Width = 360;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(new TextBlock { Text = label });
        stack.Children.Add(_value);
        var ok = new Button { Content = "OK", Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => DialogResult = true;
        stack.Children.Add(ok);
        Content = stack;
    }
}
