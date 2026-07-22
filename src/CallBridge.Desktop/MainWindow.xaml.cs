using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CallBridge.Desktop;

public partial class MainWindow : Window
{
    private const string Version = "0.13.0";
    private const string StandardSipProvider = "Standard SIP";
    private const string AxionHivePbxProvider = "Axion / HivePBX";
    private const string AxionNoixaPendingProvider = "Axion / Noixa pending";
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IT Health Technologies", "CallBridge", "settings.json");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private AppSettings _settings = new();
    private Process? _localServiceProcess;
    private string? _activeCallId;
    private bool _registered;
    private bool _muted;
    private bool _held;
    private List<RowItem> _currentRows = [];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly SipSoftphone _sip = new();
    private string? _sipCallId;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        var localToken = EnsureLocalService();
        if (!string.IsNullOrWhiteSpace(localToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", localToken);
        ApplyTemplates();
        WireEvents();
        ApplyBranding();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
        _ = RenderPhoneAsync();
        _ = RefreshHealthAsync();
        _statusTimer.Tick += (_, _) => RefreshOperationalStatus();
        _statusTimer.Start();
        _sip.RegistrationStateChanged += state => Dispatcher.Invoke(() =>
        {
            _registered = state == "registered";
            RegistrationMetric.Text = state;
            UpdateProviderStatus();
        });
        _sip.CallStateChanged += state => Dispatcher.Invoke(() =>
        {
            CallStateText.Text = state.ToUpperInvariant();
            if (state == "ended" && _activeCallId is not null)
            {
                _ = LogSipEventAsync("hangup");
                EndLocalCall("READY");
            }
        });
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            if (_localServiceProcess is { HasExited: false }) _localServiceProcess.Kill(true);
            _localServiceProcess?.Dispose();
            _http.Dispose();
            _ = _sip.DisposeAsync();
        };
    }

    private string? EnsureLocalService()
    {
        var localToken = Environment.GetEnvironmentVariable("CALLBRIDGE_LOCAL_TOKEN");
        if (!string.IsNullOrWhiteSpace(localToken)) return localToken;

        var servicePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "service", "CallBridge.Service.exe"));
        if (!File.Exists(servicePath)) return null;

        var token = NewSessionToken();
        var runtimeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IT Health Technologies", "CallBridge");
        var dataDir = Path.Combine(runtimeRoot, "data");
        var logDir = Path.Combine(runtimeRoot, "logs");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(logDir);

        var startInfo = new ProcessStartInfo(servicePath)
        {
            WorkingDirectory = Path.GetDirectoryName(servicePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["CALLBRIDGE_LOCAL_TOKEN"] = token;
        startInfo.Environment["CALLBRIDGE_CALL_RETENTION_DAYS"] = "90";
        startInfo.Environment["PORT"] = "8787";
        startInfo.Environment["DATABASE_PATH"] = Path.Combine(dataDir, "callbridge.db");
        _localServiceProcess = Process.Start(startInfo);
        return token;
    }

    private static string NewSessionToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private void WireEvents()
    {
        ProviderBox.Items.Add(StandardSipProvider);
        ProviderBox.Items.Add(AxionHivePbxProvider);
        ProviderBox.Items.Add(AxionNoixaPendingProvider);
        ProviderBox.SelectedItem = _settings.Provider;
        ProviderBox.SelectionChanged += async (_, _) =>
        {
            _settings.Provider = ProviderBox.SelectedItem?.ToString() ?? StandardSipProvider;
            ApplyProviderPreset();
            if (!IsSipBackedProvider(_settings.Provider) && _sip.IsRegistered) await _sip.UnregisterAsync();
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
        ImportContactsButton.Click += async (_, _) => await ImportContactsAsync();
        SyncConnectWiseButton.Click += async (_, _) => await SyncConnectWiseAsync();
        OpenCompanyButton.Click += (_, _) => OpenSelectedCompany();
        CreateTicketButton.Click += async (_, _) => await CreateTicketForSelectedContactAsync();
        CallNoteButton.Click += async (_, _) => await EditSelectedCallNoteAsync();
        ExportHistoryButton.Click += async (_, _) => await ExportCallHistoryAsync();
        AddContactButton.Click += async (_, _) => await AddContactAsync();
        EditContactButton.Click += async (_, _) => await EditSelectedContactAsync();
        DeleteContactButton.Click += async (_, _) => await DeleteSelectedContactAsync();
        ClearContactsButton.Click += async (_, _) => await ClearImportedContactsAsync();
        RefreshRowsButton.Click += async (_, _) => await RefreshCurrentViewAsync();
        MainList.MouseDoubleClick += async (_, _) => await ActivateSelectedRowAsync(MainList);
        RowsList.MouseDoubleClick += async (_, _) => await ActivateSelectedRowAsync(RowsList);

        DashboardButton.Click += async (_, _) => ShowRows("Dashboard", "Connector health, registration, and operational status", await DashboardRowsAsync());
        PhoneButton.Click += async (_, _) => await RenderPhoneAsync();
        ContactsButton.Click += async (_, _) => ShowRows("Contacts", "Live directory records from CallBridge", await ContactRowsAsync());
        HistoryButton.Click += async (_, _) => ShowRows("Call history", "Inbound, outbound, and missed calls", await HistoryRowsAsync());
        MessagesButton.Click += (_, _) => ShowRows("More", "Provider-dependent tools and call add-ons", MoreRows());
        VoicemailButton.Click += (_, _) => ShowRows("Voicemail", "New and saved voice messages", VoicemailRows());
        ParkingButton.Click += (_, _) => ShowRows("Parking", "Parked calls and pickup slots", ParkingRows());
        RecordingsButton.Click += (_, _) => ShowRows("Recordings", "Call recordings and review queue", RecordingRows());
        MspButton.Click += async (_, _) => ShowRows("ConnectWise", "Live ConnectWise support workflows", await MspRowsAsync());
        SettingsButton.Click += (_, _) => ShowSettings();

        foreach (Button button in DialPad.Children.OfType<Button>())
        {
            button.Click += async (_, _) =>
            {
                var digit = button.Content.ToString()![0].ToString();
                if (_activeCallId is null) DialText.Text += digit;
                else if (IsSipBackedProvider(_settings.Provider)) await _sip.SendDtmfAsync(digit);
                else await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/dtmf", new { digits = digit });
            };
        }
    }

    private async Task ActivateSelectedRowAsync(ListBox list)
    {
        if (list.SelectedItem is not RowItem row) return;

        if (row.Action.Equals("Call", StringComparison.OrdinalIgnoreCase))
        {
            var destination = row.Destination;
            if (string.IsNullOrWhiteSpace(destination)) destination = ExtractDestination(row.Detail);
            if (string.IsNullOrWhiteSpace(destination)) destination = row.Title;
            DialText.Text = destination;
            await RenderPhoneAsync();
            await ToggleCallAsync();
            return;
        }

        if (row.Action.Equals("Edit", StringComparison.OrdinalIgnoreCase) || row.Action.Equals("Open", StringComparison.OrdinalIgnoreCase))
        {
            if (row.Title.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
                row.Title.Contains("Extension", StringComparison.OrdinalIgnoreCase) ||
                row.Title.Contains("API", StringComparison.OrdinalIgnoreCase))
            {
                ShowSettings();
                return;
            }
        }

        if (row.Action.Equals("Test", StringComparison.OrdinalIgnoreCase))
        {
            await RefreshHealthAsync();
            ShowRows("Dashboard", "Connector health, registration, and operational status", await DashboardRowsAsync());
            return;
        }

        if (row.Action.Equals("Configure", StringComparison.OrdinalIgnoreCase)) { ShowSettings(); return; }
        if (row.Action.Equals("Sync", StringComparison.OrdinalIgnoreCase)) { await SyncConnectWiseAsync(); return; }

        MessageBox.Show($"{row.Title}\n\n{row.Detail}\n\nThis provider-dependent workflow is not available with the current configuration.", "CallBridge", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string ExtractDestination(string detail)
    {
        var phone = System.Text.RegularExpressions.Regex.Match(detail, @"\([0-9]{3}\)\s*[0-9]{3}-[0-9]{4}");
        if (phone.Success) return phone.Value;
        var extension = System.Text.RegularExpressions.Regex.Match(detail, @"Extension\s+[0-9]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return extension.Success ? extension.Value.Replace("Extension", "", StringComparison.OrdinalIgnoreCase).Trim() : "";
    }

    private void ApplyTemplates()
    {
        SetNav(DashboardButton, "E80F", "Dashboard");
        SetNav(PhoneButton, "E717", "Phone");
        SetNav(ContactsButton, "E77B", "Contacts");
        SetNav(HistoryButton, "E823", "Call history");
        SetNav(MessagesButton, "E712", "More");
        SetNav(VoicemailButton, "E720", "Voicemail");
        SetNav(ParkingButton, "E811", "Parking");
        SetNav(RecordingsButton, "E7C8", "Recordings");
        SetNav(MspButton, "E90F", "ConnectWise");
        SetNav(SettingsButton, "E713", "Settings");
        VoicemailButton.Visibility = Visibility.Collapsed;
        ParkingButton.Visibility = Visibility.Collapsed;
        RecordingsButton.Visibility = Visibility.Collapsed;

    }

    private static void SetNav(Button button, string glyphHex, string label)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = char.ConvertFromUtf32(Convert.ToInt32(glyphHex, 16)),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Width = 28,
            FontSize = 15,
            Foreground = Brush("#0E7FAE"),
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("#263544")
        });
        button.Content = panel;
    }


    private void LoadSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var legacyPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        var sourcePath = File.Exists(_settingsPath) ? _settingsPath : legacyPath;
        if (File.Exists(sourcePath))
        {
            try
            {
                var json = File.ReadAllText(sourcePath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions()) ?? new AppSettings();
                if (!LocalApi.TryNormalize(_settings.ApiBase, out var apiBase)) apiBase = LocalApi.DefaultBase;
                _settings.ApiBase = apiBase;
                if (_settings.Provider is "Mock provider" or "Standard SIP test") _settings.Provider = StandardSipProvider;
                if (!string.IsNullOrWhiteSpace(_settings.SipPasswordProtected)) _settings.SipPassword = CredentialProtector.Unprotect(_settings.SipPasswordProtected);
                else
                {
                    using var legacy = JsonDocument.Parse(json);
                    if (legacy.RootElement.TryGetProperty("sipPassword", out var oldPassword)) _settings.SipPassword = oldPassword.GetString() ?? "";
                }
                if (!string.IsNullOrWhiteSpace(_settings.ConnectWisePrivateKeyProtected)) _settings.ConnectWisePrivateKey = CredentialProtector.Unprotect(_settings.ConnectWisePrivateKeyProtected);
                if (!string.IsNullOrWhiteSpace(_settings.ConnectWisePlatformClientSecretProtected)) _settings.ConnectWisePlatformClientSecret = CredentialProtector.Unprotect(_settings.ConnectWisePlatformClientSecretProtected);
                if (!string.IsNullOrWhiteSpace(_settings.ConnectWisePlatformAccessTokenProtected)) _settings.ConnectWisePlatformAccessToken = CredentialProtector.Unprotect(_settings.ConnectWisePlatformAccessTokenProtected);
                if (_settings.ConnectWisePlatformAccessTokenExpiresAt <= DateTimeOffset.UtcNow)
                {
                    _settings.ConnectWisePlatformAccessToken = "";
                    _settings.ConnectWisePlatformAccessTokenProtected = "";
                    _settings.ConnectWisePlatformAccessTokenExpiresAt = null;
                }
                SaveSettings(false);
            }
            catch
            {
                _settings = new AppSettings();
                SaveSettings(false);
                MessageBox.Show("CallBridge settings could not be read and were reset to safe defaults.", "CallBridge");
            }
        }
        else
        {
            SaveSettings(false);
        }
    }

    private void SaveSettings(bool apply)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        _settings.SipPasswordProtected = string.IsNullOrWhiteSpace(_settings.SipPassword) ? "" : CredentialProtector.Protect(_settings.SipPassword);
        _settings.ConnectWisePrivateKeyProtected = string.IsNullOrWhiteSpace(_settings.ConnectWisePrivateKey) ? "" : CredentialProtector.Protect(_settings.ConnectWisePrivateKey);
        _settings.ConnectWisePlatformClientSecretProtected = string.IsNullOrWhiteSpace(_settings.ConnectWisePlatformClientSecret) ? "" : CredentialProtector.Protect(_settings.ConnectWisePlatformClientSecret);
        _settings.ConnectWisePlatformAccessTokenProtected = string.IsNullOrWhiteSpace(_settings.ConnectWisePlatformAccessToken) ? "" : CredentialProtector.Protect(_settings.ConnectWisePlatformAccessToken);
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
        VersionText.Text = $"CallBridge v{Version}";
    }

    private void ApplyResponsiveLayout()
    {
        var compact = ActualWidth > 0 && ActualWidth < 940;
        SidebarColumn.Width = new GridLength(compact ? 68 : 226);
        SidebarRoot.Margin = compact ? new Thickness(10) : new Thickness(18);
        BrandBlock.Margin = compact ? new Thickness(0, 4, 0, 12) : new Thickness(0, 6, 0, 20);
        ContentHost.Margin = compact ? new Thickness(18) : new Thickness(30);
        DialerColumn.Width = new GridLength(compact ? 300 : 360);
        PhoneGapColumn.Width = new GridLength(compact ? 14 : 22);

        foreach (var button in new[] { DashboardButton, PhoneButton, ContactsButton, HistoryButton, MspButton, MessagesButton, SettingsButton })
        {
            button.Padding = compact ? new Thickness(10, 9, 10, 9) : new Thickness(13, 11, 13, 11);
            button.Margin = compact ? new Thickness(0, 2, 0, 2) : new Thickness(0, 3, 0, 3);
            if (button.Content is StackPanel panel && panel.Children.Count > 1)
            {
                panel.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                ((FrameworkElement)panel.Children[0]).Width = compact ? 26 : 28;
                panel.Children[1].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        PresenceDetails.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarStatus.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandTitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        BrandSubtitle.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LogoText.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RenderPhoneAsync()
    {
        PageTitle.Text = "Phone";
        PhoneView.Visibility = Visibility.Visible;
        ListView.Visibility = Visibility.Collapsed;
        PanelHeading.Text = "Live directory";
        MainList.ItemsSource = (await ContactRowsAsync()).Take(5).ToList();
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
        ImportContactsButton.Visibility = title == "Contacts" ? Visibility.Visible : Visibility.Collapsed;
        ClearContactsButton.Visibility = title == "Contacts" ? Visibility.Visible : Visibility.Collapsed;
        AddContactButton.Visibility = title == "Contacts" ? Visibility.Visible : Visibility.Collapsed;
        EditContactButton.Visibility = title == "Contacts" ? Visibility.Visible : Visibility.Collapsed;
        DeleteContactButton.Visibility = title == "Contacts" ? Visibility.Visible : Visibility.Collapsed;
        SyncConnectWiseButton.Visibility = title == "Contacts" ? Visibility.Visible : Visibility.Collapsed;
        OpenCompanyButton.Visibility = title == "Contacts" ? Visibility.Visible : Visibility.Collapsed;
        CreateTicketButton.Visibility = title == "Contacts" ? Visibility.Visible : Visibility.Collapsed;
        CallNoteButton.Visibility = title == "Call history" ? Visibility.Visible : Visibility.Collapsed;
        ExportHistoryButton.Visibility = title == "Call history" ? Visibility.Visible : Visibility.Collapsed;
        RenderRows(rows);
    }

    private async Task AddContactAsync()
    {
        var editor = new ContactWindow { Owner = this };
        if (editor.ShowDialog() != true) return;
        await SaveContactAsync(editor, null);
    }

    private async Task EditSelectedContactAsync()
    {
        if (RowsList.SelectedItem is not RowItem row || string.IsNullOrWhiteSpace(row.ContactId))
        {
            MessageBox.Show("Select a contact first.", "CallBridge"); return;
        }
        var editor = new ContactWindow(row.Title, row.Company, row.Destination) { Owner = this };
        if (editor.ShowDialog() != true) return;
        await SaveContactAsync(editor, row.ContactId);
    }

    private async Task SaveContactAsync(ContactWindow editor, string? contactId)
    {
        try
        {
            var payload = new { companyName = editor.CompanyName, contactName = editor.ContactName, phones = new[] { editor.Phone } };
            var json = JsonSerializer.Serialize(payload, JsonOptions());
            var path = contactId is null ? "/directory/contacts" : $"/directory/contacts/{Uri.EscapeDataString(contactId)}";
            using var request = new HttpRequestMessage(contactId is null ? HttpMethod.Post : HttpMethod.Put, $"{_settings.ApiBase.TrimEnd('/')}{path}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            ShowRows("Contacts", "Live directory records from CallBridge", await ContactRowsAsync());
        }
        catch (Exception ex) { MessageBox.Show($"Could not save contact: {ex.Message}", "CallBridge"); }
    }

    private async Task DeleteSelectedContactAsync()
    {
        if (RowsList.SelectedItem is not RowItem row || string.IsNullOrWhiteSpace(row.ContactId))
        {
            MessageBox.Show("Select a contact first.", "CallBridge"); return;
        }
        if (MessageBox.Show($"Delete {row.Title}?", "CallBridge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            using var response = await _http.DeleteAsync($"{_settings.ApiBase.TrimEnd('/')}/directory/contacts/{Uri.EscapeDataString(row.ContactId)}");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            ShowRows("Contacts", "Live directory records from CallBridge", await ContactRowsAsync());
        }
        catch (Exception ex) { MessageBox.Show($"Could not delete contact: {ex.Message}", "CallBridge"); }
    }

    private async Task RefreshCurrentViewAsync()
    {
        if (PageTitle.Text == "Contacts") ShowRows("Contacts", "Live directory records from CallBridge", await ContactRowsAsync());
        else if (PageTitle.Text == "Call history") ShowRows("Call history", "Inbound, outbound, and missed calls", await HistoryRowsAsync());
        else if (PageTitle.Text == "Dashboard") ShowRows("Dashboard", "Connector health, registration, and operational status", await DashboardRowsAsync());
    }

    private async Task ImportContactsAsync()
    {
        var picker = new OpenFileDialog { Filter = "CSV contact files (*.csv)|*.csv", Title = "Import CallBridge contacts" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var lines = await File.ReadAllLinesAsync(picker.FileName);
            if (lines.Length < 2) throw new InvalidDataException("The CSV must contain a header and at least one contact.");
            var headers = ParseCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
            int companyIndex = FindHeader(headers, "company", "companyname", "organization");
            int contactIndex = FindHeader(headers, "contact", "contactname", "name");
            int phoneIndex = FindHeader(headers, "phone", "phonenumber", "number", "mobile");
            if (companyIndex < 0 || phoneIndex < 0) throw new InvalidDataException("Required columns: Company and Phone. Contact is optional.");
            var records = new List<object>();
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var cells = ParseCsvLine(lines[i]);
                var company = Cell(cells, companyIndex);
                var contact = Cell(cells, contactIndex);
                var phone = Cell(cells, phoneIndex);
                if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(phone)) throw new InvalidDataException($"Row {i + 1} requires Company and Phone.");
                records.Add(new { companyName = company, contactName = contact, phones = new[] { phone } });
            }
            if (records.Count == 0) throw new InvalidDataException("No contacts were found in the CSV.");
            var json = JsonSerializer.Serialize(new { records }, JsonOptions());
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{_settings.ApiBase.TrimEnd('/')}/directory") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            ShowRows("Contacts", "Live directory records from CallBridge", await ContactRowsAsync());
            MessageBox.Show($"Imported {records.Count} contacts. They are saved in CallBridge and ready to search or dial.", "CallBridge");
        }
        catch (Exception ex) { MessageBox.Show($"Import failed: {ex.Message}", "CallBridge"); }
    }

    private async Task SyncConnectWiseAsync()
    {
        if (!ConnectWiseClient.IsConfigured(_settings))
        {
            MessageBox.Show("Configure ConnectWise Site, Company ID, API keys, and Client ID in Settings first.", "CallBridge");
            ShowSettings(); return;
        }
        try
        {
            SyncConnectWiseButton.IsEnabled = false;
            var progress = new Progress<string>(message => ListSubheading.Text = message);
            using var client = new ConnectWiseClient(_settings);
            var test = await client.TestAsync();
            if (!test.Success) throw new HttpRequestException(test.Message);
            var contacts = await client.DownloadContactsAsync(progress);
            var records = contacts.Select(contact => new { companyId = contact.CompanyId, companyName = contact.CompanyName, contactId = contact.ContactId, contactName = contact.ContactName, phones = contact.Phones }).ToList();
            var json = JsonSerializer.Serialize(new { records }, JsonOptions());
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{_settings.ApiBase.TrimEnd('/')}/integrations/connectwise/directory") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            ShowRows("Contacts", "Live ConnectWise PSA directory", await ContactRowsAsync());
            MessageBox.Show($"Synchronized {contacts.Count} ConnectWise contacts with callable phone records.", "CallBridge");
        }
        catch (Exception ex) { MessageBox.Show($"ConnectWise sync failed: {ex.Message}", "CallBridge"); }
        finally { SyncConnectWiseButton.IsEnabled = true; }
    }

    private void OpenSelectedCompany()
    {
        if (RowsList.SelectedItem is not RowItem row || string.IsNullOrWhiteSpace(row.CompanyId)) { MessageBox.Show("Select a synchronized ConnectWise contact first.", "CallBridge"); return; }
        if (!ConnectWiseClient.IsConfigured(_settings)) { MessageBox.Show("Configure ConnectWise in Settings first.", "CallBridge"); return; }
        try { using var client = new ConnectWiseClient(_settings); Process.Start(new ProcessStartInfo(client.CompanyUrl(row.CompanyId)) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show($"Could not open company: {ex.Message}", "CallBridge"); }
    }

    private async Task CreateTicketForSelectedContactAsync()
    {
        if (RowsList.SelectedItem is not RowItem row || string.IsNullOrWhiteSpace(row.CompanyId)) { MessageBox.Show("Select a synchronized ConnectWise contact first.", "CallBridge"); return; }
        if (!ConnectWiseClient.IsConfigured(_settings) || _settings.ConnectWiseBoardId <= 0) { MessageBox.Show("Configure ConnectWise and a default service board ID in Settings first.", "CallBridge"); return; }
        var editor = new TicketWindow(row.Company, row.Title, row.Destination) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try
        {
            using var client = new ConnectWiseClient(_settings);
            var ticket = await client.CreateTicketAsync(row.CompanyId, _settings.ConnectWiseBoardId, editor.Summary, editor.Description);
            var ticketId = Value(ticket, "id");
            MessageBox.Show($"ConnectWise ticket {ticketId} was created successfully.", "CallBridge");
            if (!string.IsNullOrWhiteSpace(ticketId)) Process.Start(new ProcessStartInfo(client.TicketUrl(ticketId)) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show($"Ticket creation failed: {ex.Message}", "CallBridge"); }
    }

    private async Task ClearImportedContactsAsync()
    {
        if (MessageBox.Show("Remove all contacts previously loaded by CSV import?", "CallBridge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            using var response = await _http.DeleteAsync($"{_settings.ApiBase.TrimEnd('/')}/directory");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            ShowRows("Contacts", "Live directory records from CallBridge", await ContactRowsAsync());
        }
        catch (Exception ex) { MessageBox.Show($"Could not clear imported contacts: {ex.Message}", "CallBridge"); }
    }

    private static int FindHeader(List<string> headers, params string[] names) => headers.FindIndex(h => names.Contains(h.Replace(" ", "")));
    private static string Cell(List<string> cells, int index) => index >= 0 && index < cells.Count ? cells[index].Trim() : "";
    private static List<string> ParseCsvLine(string line)
    {
        var cells = new List<string>(); var value = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { cells.Add(value.ToString()); value.Clear(); }
            else value.Append(c);
        }
        cells.Add(value.ToString()); return cells;
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


    private static bool IsSipBackedProvider(string provider) =>
        provider is StandardSipProvider or AxionHivePbxProvider;

    private void ApplyProviderPreset()
    {
        // Provider presets must not ship tenant-specific SIP domains or credentials.
    }
    private async Task ToggleRegistrationAsync()
    {
        ApplyProviderPreset();
        if (_settings.Provider == AxionNoixaPendingProvider)
        {
            MessageBox.Show("Axion/Noixa calling requires their approved SBC/WebRTC integration details. This provider is intentionally blocked until those are supplied.", "CallBridge");
            return;
        }

        if (IsSipBackedProvider(_settings.Provider))
        {
            if (_sip.IsRegistered)
            {
                await _sip.UnregisterAsync();
                _registered = false;
                UpdateProviderStatus();
                return;
            }
            if (string.IsNullOrWhiteSpace(_settings.SipServer) || string.IsNullOrWhiteSpace(_settings.SipUsername) || string.IsNullOrWhiteSpace(_settings.SipPassword))
            {
                MessageBox.Show("Add the SIP server, SBC extension username, and password in Settings first.", "CallBridge");
                return;
            }
            RegistrationMetric.Text = "Registering...";
            var sipResult = await _sip.RegisterAsync(_settings.SipServer, _settings.SipUsername, _settings.SipPassword);
            _registered = sipResult.Success;
            UpdateProviderStatus();
            if (!sipResult.Success) ShowApiError(sipResult.Message);
            return;
        }
        MessageBox.Show("Select a SIP-backed provider and add registrar credentials before registering.", "CallBridge");
    }

    private void RefreshOperationalStatus()
    {
        if (IsSipBackedProvider(_settings.Provider))
        {
            _registered = _sip.IsRegistered;
            UpdateProviderStatus();
            return;
        }
        if (_settings.Provider == AxionNoixaPendingProvider)
        {
            _registered = false;
            UpdateProviderStatus();
            return;
        }
        _registered = false;
        UpdateProviderStatus();
    }

    private void UpdateProviderStatus()
    {
        RegistrationMetric.Text = _registered ? "Registered" : "Not registered";
        RegisterButton.Content = _registered ? "Unregister" : "Register";
        PresenceText.Text = _registered ? "Available" : "Offline";
        PresenceDot.Fill = _registered ? Brush("#42B7A8") : Brush("#DFAE4B");
        CallStateText.Text = _registered ? $"READY - {_settings.Provider.ToUpperInvariant()}" : "READY";
    }

    private async Task ToggleCallAsync()
    {
        if (_activeCallId is not null)
        {
            if (IsSipBackedProvider(_settings.Provider))
            {
                await _sip.HangupAsync();
                await LogSipEventAsync("hangup");
                EndLocalCall("READY");
                return;
            }
            var hangup = await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/hangup", new { });
            if (!hangup.Success) { ShowApiError(hangup.Message); return; }
            EndLocalCall("READY");
            return;
        }

        if (string.IsNullOrWhiteSpace(DialText.Text)) return;
        if (!_registered)
        {
            MessageBox.Show("Register the selected provider before starting a call.", "CallBridge");
            return;
        }

        if (IsSipBackedProvider(_settings.Provider))
        {
            _sipCallId = Guid.NewGuid().ToString("N");
            _activeCallId = _sipCallId;
            ActiveCallMetric.Text = DialText.Text;
            CallButton.Content = "End call";
            CallButton.Background = Brush("#C95669");
            CallStateText.Text = "DIALING";
            Topmost = _settings.AlwaysOnTopDuringCalls;
            await LogSipEventAsync("started");
            var result = await _sip.DialAsync(DialText.Text);
            if (!result.Success)
            {
                await LogSipEventAsync("failed");
                EndLocalCall("CALL FAILED");
                ShowApiError(result.Message);
                return;
            }
            CallStateText.Text = $"CONNECTED - {_settings.Provider.ToUpperInvariant()}";
            await LogSipEventAsync("connected");
            return;
        }

        MessageBox.Show("The selected provider cannot place calls until its approved integration is configured.", "CallBridge");
    }

    private void EndLocalCall(string state)
    {
        _activeCallId = null;
        _sipCallId = null;
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
        var requested = !_muted;
        if (IsSipBackedProvider(_settings.Provider))
        {
            try { await _sip.SetMutedAsync(requested); _muted = requested; MuteButton.Content = _muted ? "Unmute" : "Mute"; }
            catch (Exception ex) { ShowApiError(ex.Message); }
            return;
        }
        var result = await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/mute", new { muted = requested });
        if (!result.Success) { ShowApiError(result.Message); return; }
        _muted = requested;
        MuteButton.Content = _muted ? "Unmute" : "Mute";
    }

    private async Task ToggleHoldAsync()
    {
        if (_activeCallId is null) return;
        var requested = !_held;
        if (IsSipBackedProvider(_settings.Provider))
        {
            try
            {
                await _sip.SetHoldAsync(requested); _held = requested;
                HoldButton.Content = _held ? "Resume" : "Hold"; CallStateText.Text = _held ? "CALL ON HOLD" : "CONNECTED";
                await LogSipEventAsync(_held ? "hold" : "resume");
            }
            catch (Exception ex) { ShowApiError(ex.Message); }
            return;
        }
        var result = await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/{(requested ? "hold" : "resume")}", new { });
        if (!result.Success) { ShowApiError(result.Message); return; }
        _held = requested;
        HoldButton.Content = _held ? "Resume" : "Hold";
        CallStateText.Text = _held ? "CALL ON HOLD" : "CONNECTED";
    }

    private async Task TransferAsync()
    {
        if (_activeCallId is null) return;
        var prompt = new PromptWindow("Transfer call", "Destination extension or phone number");
        if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.Value))
        {
            if (IsSipBackedProvider(_settings.Provider))
            {
                var sipResult = await _sip.TransferAsync(prompt.Value);
                if (!sipResult.Success) { ShowApiError(sipResult.Message); return; }
                await LogSipEventAsync("transfer");
                EndLocalCall($"TRANSFERRED TO {prompt.Value}");
                return;
            }
            var result = await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/transfer", new { destination = prompt.Value });
            if (!result.Success) { ShowApiError(result.Message); return; }
            EndLocalCall($"TRANSFERRED TO {prompt.Value}");
        }
    }

    private async Task SendDtmfAsync()
    {
        if (_activeCallId is null) return;
        var prompt = new PromptWindow("Send DTMF", "Digits");
        if (prompt.ShowDialog() == true && !string.IsNullOrWhiteSpace(prompt.Value))
        {
            if (IsSipBackedProvider(_settings.Provider))
            {
                try { await _sip.SendDtmfAsync(prompt.Value); } catch (Exception ex) { ShowApiError(ex.Message); }
                return;
            }
            var result = await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/dtmf", new { digits = prompt.Value });
            if (!result.Success) ShowApiError(result.Message);
        }
    }

    private async Task<TelephonyResult> PostTelephonyAsync(string path, object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions());
            using var response = await _http.PostAsync($"{_settings.ApiBase.TrimEnd('/')}{path}", new StringContent(json, Encoding.UTF8, "application/json"));
            var responseText = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseText);
            if (!response.IsSuccessStatusCode)
            {
                var message = doc.RootElement.TryGetProperty("message", out var error) ? error.GetString() : $"Connector returned HTTP {(int)response.StatusCode}.";
                return new(false, null, message ?? "The connector rejected the operation.");
            }
            var callId = doc.RootElement.TryGetProperty("call", out var call) && call.TryGetProperty("id", out var id) ? id.GetString() : null;
            return new(true, callId, "");
        }
        catch (Exception ex)
        {
            ApiMetric.Text = "Offline";
            return new(false, null, $"Connector unavailable: {ex.Message}");
        }
    }

    private static void ShowApiError(string message) => MessageBox.Show(message, "CallBridge", MessageBoxButton.OK, MessageBoxImage.Warning);

    private async Task LogSipEventAsync(string state)
    {
        if (string.IsNullOrWhiteSpace(_sipCallId)) return;
        try
        {
            var payload = new { callId = _sipCallId, direction = "outbound", state, callerNumber = _settings.CallerId, calledNumber = DialText.Text, extension = _settings.Extension, occurredAt = DateTime.UtcNow.ToString("O") };
            var json = JsonSerializer.Serialize(payload, JsonOptions());
            using var response = await _http.PostAsync($"{_settings.ApiBase.TrimEnd('/')}/calls/events", new StringContent(json, Encoding.UTF8, "application/json"));
        }
        catch { }
    }

    private async Task<List<RowItem>> HistoryRowsAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"{_settings.ApiBase.TrimEnd('/')}/calls/journal?limit=100");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("calls", out var calls))
            {
                var rows = calls.EnumerateArray().Select(call =>
                {
                    var direction = Value(call, "direction");
                    var number = direction.Equals("outbound", StringComparison.OrdinalIgnoreCase) ? Value(call, "calledNumber") : Value(call, "callerNumber");
                    var duration = int.TryParse(Value(call, "durationSeconds"), out var seconds) ? TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss") : "00:00";
                    var company = Value(call, "companyName");
                    var notes = Value(call, "notes");
                    var outcome = Value(call, "outcome");
                    var detail = $"{direction} Â· {Value(call, "state")} Â· {duration} Â· {Value(call, "startedAt")}";
                    if (!string.IsNullOrWhiteSpace(company)) detail += $" Â· {company}";
                    if (!string.IsNullOrWhiteSpace(outcome)) detail += $" Â· {outcome}";
                    return new RowItem(string.IsNullOrWhiteSpace(number) ? "Unknown number" : number, detail, "Call", number, Company: company, CompanyId: Value(call, "companyId"), CallId: Value(call, "callId"), Notes: notes, Outcome: outcome);
                }).ToList();
                return rows.Count > 0 ? rows : EmptyRows("No live call history is available yet. Completed and missed calls will appear here after real traffic is received.");
            }
        }
        catch { }
        return EmptyRows("Call history endpoint is unavailable. Start CallBridge Internal and check Settings > Test API.");
    }

    private async Task EditSelectedCallNoteAsync()
    {
        if (RowsList.SelectedItem is not RowItem row || string.IsNullOrWhiteSpace(row.CallId)) { MessageBox.Show("Select a call first.", "CallBridge"); return; }
        var editor = new CallNoteWindow(row.Title, row.Notes, row.Outcome) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try
        {
            var json = JsonSerializer.Serialize(new { notes = editor.Notes, outcome = editor.Outcome }, JsonOptions());
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{_settings.ApiBase.TrimEnd('/')}/calls/{Uri.EscapeDataString(row.CallId)}/notes") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            ShowRows("Call history", "Inbound, outbound, and missed calls", await HistoryRowsAsync());
        }
        catch (Exception ex) { MessageBox.Show($"Could not save call notes: {ex.Message}", "CallBridge"); }
    }

    private async Task ExportCallHistoryAsync()
    {
        var rows = await HistoryRowsAsync();
        var picker = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = $"CallBridge-call-history-{DateTime.Now:yyyy-MM-dd}.csv", Title = "Export call history" };
        if (picker.ShowDialog(this) != true) return;
        var csv = new StringBuilder("Number,Details,Company,Outcome,Notes\r\n");
        foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.CallId))) csv.AppendLine(string.Join(',', Csv(row.Title), Csv(row.Detail), Csv(row.Company), Csv(row.Outcome), Csv(row.Notes)));
        await File.WriteAllTextAsync(picker.FileName, csv.ToString(), Encoding.UTF8);
    }

    private static string Csv(string value)
    {
        var safe = value.Replace("\r", " ").Replace("\n", " ");
        if (safe.Length > 0 && safe[0] is '=' or '+' or '-' or '@' or '\t') safe = "'" + safe;
        return $"\"{safe.Replace("\"", "\"\"")}\"";
    }

    private static string Value(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.ToString() : "";

    private async Task<List<RowItem>> DashboardRowsAsync()
    {
        await RefreshHealthAsync();
        var liveDirectoryCount = (await ContactRowsAsync()).Count(r => r.Action == "Call");
        return
        [
            new("Provider", $"{_settings.Provider} - {(_registered ? "registered" : "not registered")}", "Open"),
            new("Connector API", $"{_settings.ApiBase} - {ApiMetric.Text}", "Test"),
            new("Extension", $"{_settings.Extension} - caller ID {_settings.CallerId}", "Edit"),
            new("Directory", $"{liveDirectoryCount} live callable records", "Open"),
            new("Axion status", "Blocked until official SBC/WebRTC integration details are supplied", "Review")
        ];
    }

    private async Task<List<RowItem>> ContactRowsAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"{_settings.ApiBase.TrimEnd('/')}/directory?limit=5000");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("records", out var records))
            {
                var rows = records.EnumerateArray().Select(record =>
                {
                    var company = Value(record, "companyName");
                    var contact = Value(record, "contactName");
                    var phone = Value(record, "phone");
                    var contactId = Value(record, "contactId");
                    var companyId = Value(record, "companyId");
                    var title = string.IsNullOrWhiteSpace(contact) ? company : contact;
                    return new RowItem(title, $"{phone} - {company}", "Call", phone, contactId, company, companyId);
                }).Where(r => !string.IsNullOrWhiteSpace(r.Title) && !string.IsNullOrWhiteSpace(r.Detail)).ToList();
                return rows.Count > 0 ? rows : EmptyRows("No live directory records are available yet. Connect a real directory sync source to populate contacts.");
            }
        }
        catch
        {
            return EmptyRows("Directory endpoint is unavailable. Start CallBridge Internal and check Settings > Test API.");
        }
        return EmptyRows("No live directory records are available yet.");
    }

    private static List<RowItem> EmptyRows(string message) => [new("No live data", message, "Open")];

    private static List<RowItem> MessageRows() =>
    [
        new("No live messages", "SMS/chat provider integration is not connected yet.", "Open")
    ];
    private static List<RowItem> MoreRows() =>
    [
        new("Messages", "SMS/chat provider integration is not connected yet.", "Unavailable"),
        new("Voicemail", "Voicemail provider integration is not connected yet.", "Unavailable"),
        new("Call parking", "Call parking integration is not connected yet.", "Unavailable"),
        new("Recordings", "Recording provider integration is not connected yet.", "Unavailable")
    ];

    private static List<RowItem> VoicemailRows() =>
    [
        new("No live voicemail", "Voicemail provider integration is not connected yet.", "Open")
    ];

    private static List<RowItem> ParkingRows() =>
    [
        new("No live parking slots", "Call parking integration is not connected yet.", "Open")
    ];

    private static List<RowItem> RecordingRows() =>
    [
        new("No live recordings", "Recording provider integration is not connected yet.", "Open")
    ];

    private async Task<List<RowItem>> MspRowsAsync()
    {
        var contacts = await ContactRowsAsync();
        var liveCount = contacts.Count(r => r.Action == "Call");
        return
        [
            new("ConnectWise Platform OAuth", ConnectWisePlatformClient.IsConfigured(_settings) ? "Configured - use Test Platform OAuth in Settings to validate" : "Not configured", "Configure"),
            new("ConnectWise PSA", ConnectWiseClient.IsConfigured(_settings) ? "Configured - use Test ConnectWise in Settings to validate" : "Not configured", "Configure"),
            new("Directory synchronization", $"{liveCount} callable contacts currently indexed", "Sync"),
            new("Company and ticket actions", "Select a ConnectWise contact, then use Open company or New ticket", "Open")
        ];
    }

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

public sealed record RowItem(string Title, string Detail, string Action, string Destination = "", string ContactId = "", string Company = "", string CompanyId = "", string CallId = "", string Notes = "", string Outcome = "")
{
    public override string ToString() => $"{Title}\n{Detail}    {Action}";
}

public sealed record TelephonyResult(bool Success, string? CallId, string Message);

public static class LocalApi
{
    public const string DefaultBase = "http://127.0.0.1:8787";

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(value?.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}

public static class CredentialProtector
{
    private const uint UiForbidden = 0x1;
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("Kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

    public static string Protect(string value) => Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(value), true));
    public static string Unprotect(string value) => Encoding.UTF8.GetString(Transform(Convert.FromBase64String(value), false));

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, "CallBridge SIP credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, output.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}

public sealed class AppSettings
{
    public string ApiBase { get; set; } = "http://127.0.0.1:8787";
    public string Extension { get; set; } = "201";
    public string CallerId { get; set; } = "201";
    public string Provider { get; set; } = "Standard SIP";
    public string SipServer { get; set; } = "";
    public string SipUsername { get; set; } = "";
    [JsonIgnore]
    public string SipPassword { get; set; } = "";
    public string SipPasswordProtected { get; set; } = "";
    public string SipTransport { get; set; } = "UDP";
    public string Microphone { get; set; } = "Windows default communications device";
    public string Speaker { get; set; } = "Windows default communications device";
    public bool LaunchAtStartup { get; set; }
    public bool AlwaysOnTopDuringCalls { get; set; } = true;
    public string ProductName { get; set; } = "CallBridge";
    public string CompanyName { get; set; } = "IT Health Technologies";
    public string ConnectWiseSite { get; set; } = "";
    public string ConnectWiseCompanyId { get; set; } = "";
    public string ConnectWisePublicKey { get; set; } = "";
    [JsonIgnore]
    public string ConnectWisePrivateKey { get; set; } = "";
    public string ConnectWisePrivateKeyProtected { get; set; } = "";
    public string ConnectWiseClientId { get; set; } = "";
    public int ConnectWiseBoardId { get; set; }
    public string ConnectWisePlatformBaseUrl { get; set; } = ConnectWisePlatformClient.NorthAmericaBaseUrl;
    public string ConnectWisePlatformClientId { get; set; } = "";
    [JsonIgnore]
    public string ConnectWisePlatformClientSecret { get; set; } = "";
    public string ConnectWisePlatformClientSecretProtected { get; set; } = "";
    public string ConnectWisePlatformScopes { get; set; } = "platform.companies.read platform.tickets.create";
    [JsonIgnore]
    public string ConnectWisePlatformAccessToken { get; set; } = "";
    public string ConnectWisePlatformAccessTokenProtected { get; set; } = "";
    public DateTimeOffset? ConnectWisePlatformAccessTokenExpiresAt { get; set; }
}

public sealed class SettingsWindow : Window
{
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 10, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(102, 117, 134)) };
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
    private readonly TextBox _cwSite = Field();
    private readonly TextBox _cwCompany = Field();
    private readonly TextBox _cwPublic = Field();
    private readonly PasswordBox _cwPrivate = new() { Padding = new Thickness(8) };
    private readonly TextBox _cwClientId = Field();
    private readonly TextBox _cwBoardId = Field();
    private readonly ComboBox _cwPlatformBase = new() { IsEditable = true };
    private readonly TextBox _cwPlatformClientId = Field();
    private readonly PasswordBox _cwPlatformClientSecret = new() { Padding = new Thickness(8) };
    private readonly TextBox _cwPlatformScopes = Field();
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
            SipPasswordProtected = settings.SipPasswordProtected,
            SipTransport = settings.SipTransport,
            Microphone = settings.Microphone,
            Speaker = settings.Speaker,
            LaunchAtStartup = settings.LaunchAtStartup,
            AlwaysOnTopDuringCalls = settings.AlwaysOnTopDuringCalls,
            ProductName = settings.ProductName,
            CompanyName = settings.CompanyName,
            ConnectWiseSite = settings.ConnectWiseSite,
            ConnectWiseCompanyId = settings.ConnectWiseCompanyId,
            ConnectWisePublicKey = settings.ConnectWisePublicKey,
            ConnectWisePrivateKey = settings.ConnectWisePrivateKey,
            ConnectWisePrivateKeyProtected = settings.ConnectWisePrivateKeyProtected,
            ConnectWiseClientId = settings.ConnectWiseClientId,
            ConnectWiseBoardId = settings.ConnectWiseBoardId,
            ConnectWisePlatformBaseUrl = settings.ConnectWisePlatformBaseUrl,
            ConnectWisePlatformClientId = settings.ConnectWisePlatformClientId,
            ConnectWisePlatformClientSecret = settings.ConnectWisePlatformClientSecret,
            ConnectWisePlatformClientSecretProtected = settings.ConnectWisePlatformClientSecretProtected,
            ConnectWisePlatformScopes = settings.ConnectWisePlatformScopes,
            ConnectWisePlatformAccessToken = settings.ConnectWisePlatformAccessToken,
            ConnectWisePlatformAccessTokenProtected = settings.ConnectWisePlatformAccessTokenProtected,
            ConnectWisePlatformAccessTokenExpiresAt = settings.ConnectWisePlatformAccessTokenExpiresAt
        };

        Title = "Settings";
        Width = 620;
        Height = 760;
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
        _cwSite.Text = Settings.ConnectWiseSite;
        _cwCompany.Text = Settings.ConnectWiseCompanyId;
        _cwPublic.Text = Settings.ConnectWisePublicKey;
        _cwPrivate.Password = Settings.ConnectWisePrivateKey;
        _cwClientId.Text = Settings.ConnectWiseClientId;
        _cwBoardId.Text = Settings.ConnectWiseBoardId == 0 ? "" : Settings.ConnectWiseBoardId.ToString();
        foreach (var item in new[] { ConnectWisePlatformClient.NorthAmericaBaseUrl, ConnectWisePlatformClient.EuropeBaseUrl, ConnectWisePlatformClient.AustraliaBaseUrl }) _cwPlatformBase.Items.Add(item);
        _cwPlatformBase.Text = Settings.ConnectWisePlatformBaseUrl;
        _cwPlatformClientId.Text = Settings.ConnectWisePlatformClientId;
        _cwPlatformClientSecret.Password = Settings.ConnectWisePlatformClientSecret;
        _cwPlatformScopes.Text = Settings.ConnectWisePlatformScopes;
        foreach (var item in new[] { "Standard SIP", "Axion / HivePBX", "Axion / Noixa pending" }) _provider.Items.Add(item);
        _provider.SelectedItem = Settings.Provider;
        _transport.Items.Add("UDP");
        _transport.SelectedItem = "UDP";
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
        stack.Children.Add(Label("SIP provider"));
        Add(stack, "SIP server / domain", _sipServer);
        Add(stack, "SIP username", _sipUser);
        Add(stack, "SIP password", _sipPassword);
        Add(stack, "SIP transport", _transport);
        stack.Children.Add(Label("ConnectWise Platform OAuth (Developer Access)"));
        Add(stack, "Platform API URL / region", _cwPlatformBase);
        Add(stack, "OAuth Client ID", _cwPlatformClientId);
        Add(stack, "OAuth Client Secret", _cwPlatformClientSecret);
        Add(stack, "Scopes", _cwPlatformScopes);
        stack.Children.Add(Label("ConnectWise PSA API Member"));
        Add(stack, "Site URL (for example https://na.myconnectwise.net)", _cwSite);
        Add(stack, "Company ID", _cwCompany);
        Add(stack, "Public key", _cwPublic);
        Add(stack, "Private key", _cwPrivate);
        Add(stack, "Client ID", _cwClientId);
        Add(stack, "Default service board ID", _cwBoardId);
        stack.Children.Add(Label("White label"));
        Add(stack, "Product name", _product);
        Add(stack, "Company name", _company);
        stack.Children.Add(_startup);
        stack.Children.Add(_topmost);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        var test = new Button { Content = "Test API", Padding = new Thickness(14), Margin = new Thickness(0, 0, 10, 0) };
        var testPlatform = new Button { Content = "Test Platform OAuth", Padding = new Thickness(14), Margin = new Thickness(0, 0, 10, 0) };
        var testConnectWise = new Button { Content = "Test PSA", Padding = new Thickness(14), Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = "Save settings", Padding = new Thickness(14), Background = new SolidColorBrush(Color.FromRgb(17, 136, 182)), Foreground = Brushes.White };
        test.Click += async (_, _) => await TestApiAsync();
        testPlatform.Click += async (_, _) => await TestPlatformAsync();
        testConnectWise.Click += async (_, _) => await TestConnectWiseAsync();
        save.Click += (_, _) =>
        {
            if (!LocalApi.TryNormalize(_api.Text, out var apiBase))
            {
                _status.Text = "The CallBridge API must use HTTP on the local computer.";
                _status.Foreground = Brushes.Firebrick;
                return;
            }
            if (string.IsNullOrWhiteSpace(_extension.Text))
            {
                _status.Text = "Extension is required.";
                _status.Foreground = Brushes.Firebrick;
                return;
            }
            Settings.ApiBase = apiBase;
            Settings.Extension = _extension.Text;
            Settings.CallerId = _callerId.Text;
            Settings.Provider = _provider.SelectedItem?.ToString() ?? "Standard SIP";
            Settings.SipServer = _sipServer.Text;
            Settings.SipUsername = _sipUser.Text;
            Settings.SipPassword = _sipPassword.Password;
            Settings.SipTransport = "UDP";
            Settings.ProductName = _product.Text;
            Settings.CompanyName = _company.Text;
            Settings.ConnectWiseSite = _cwSite.Text.Trim();
            Settings.ConnectWiseCompanyId = _cwCompany.Text.Trim();
            Settings.ConnectWisePublicKey = _cwPublic.Text.Trim();
            Settings.ConnectWisePrivateKey = _cwPrivate.Password;
            Settings.ConnectWiseClientId = _cwClientId.Text.Trim();
            Settings.ConnectWiseBoardId = int.TryParse(_cwBoardId.Text, out var boardId) ? boardId : 0;
            ApplyPlatformFields();
            Settings.LaunchAtStartup = _startup.IsChecked == true;
            Settings.AlwaysOnTopDuringCalls = _topmost.IsChecked == true;
            SetStartup(Settings.LaunchAtStartup);
            DialogResult = true;
        };
        buttons.Children.Add(test);
        buttons.Children.Add(testPlatform);
        buttons.Children.Add(testConnectWise);
        buttons.Children.Add(save);
        stack.Children.Add(buttons);
        stack.Children.Add(_status);
        return stack;
    }

    private async Task TestApiAsync()
    {
        if (!LocalApi.TryNormalize(_api.Text, out var apiBase) || !Uri.TryCreate(apiBase, UriKind.Absolute, out var baseUri))
        {
            _status.Text = "The CallBridge API must use HTTP on the local computer.";
            _status.Foreground = Brushes.Firebrick;
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = await client.GetAsync(new Uri(baseUri, "/health"));
            _status.Text = response.IsSuccessStatusCode ? "Connector API responded successfully." : $"Connector returned HTTP {(int)response.StatusCode}.";
            _status.Foreground = response.IsSuccessStatusCode ? Brushes.SeaGreen : Brushes.Firebrick;
        }
        catch (Exception ex)
        {
            _status.Text = $"Connection failed: {ex.Message}";
            _status.Foreground = Brushes.Firebrick;
        }
    }

    private async Task TestConnectWiseAsync()
    {
        var candidate = new AppSettings { ConnectWiseSite = _cwSite.Text.Trim(), ConnectWiseCompanyId = _cwCompany.Text.Trim(), ConnectWisePublicKey = _cwPublic.Text.Trim(), ConnectWisePrivateKey = _cwPrivate.Password, ConnectWiseClientId = _cwClientId.Text.Trim() };
        if (!ConnectWiseClient.IsConfigured(candidate))
        {
            _status.Text = "Enter Site, Company ID, Public Key, Private Key, and Client ID.";
            _status.Foreground = Brushes.Firebrick; return;
        }
        try
        {
            using var client = new ConnectWiseClient(candidate);
            var result = await client.TestAsync();
            _status.Text = result.Message;
            _status.Foreground = result.Success ? Brushes.SeaGreen : Brushes.Firebrick;
        }
        catch (Exception ex) { _status.Text = ex.Message; _status.Foreground = Brushes.Firebrick; }
    }

    private async Task TestPlatformAsync()
    {
        ApplyPlatformFields();
        if (!ConnectWisePlatformClient.IsConfigured(Settings))
        {
            _status.Text = "Enter the Platform API URL, OAuth Client ID, Client Secret, and scopes.";
            _status.Foreground = Brushes.Firebrick;
            return;
        }
        try
        {
            using var client = new ConnectWisePlatformClient(Settings);
            var result = await client.TestAsync();
            _status.Text = result.Message;
            _status.Foreground = result.Success ? Brushes.SeaGreen : Brushes.Firebrick;
        }
        catch (Exception ex) { _status.Text = ex.Message; _status.Foreground = Brushes.Firebrick; }
    }

    private void ApplyPlatformFields()
    {
        var baseUrl = _cwPlatformBase.Text.Trim();
        var clientId = _cwPlatformClientId.Text.Trim();
        var clientSecret = _cwPlatformClientSecret.Password;
        var scopes = _cwPlatformScopes.Text.Trim();
        var changed = !string.Equals(Settings.ConnectWisePlatformBaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Settings.ConnectWisePlatformClientId, clientId, StringComparison.Ordinal)
            || !string.Equals(Settings.ConnectWisePlatformClientSecret, clientSecret, StringComparison.Ordinal)
            || !string.Equals(Settings.ConnectWisePlatformScopes, scopes, StringComparison.Ordinal);
        Settings.ConnectWisePlatformBaseUrl = baseUrl;
        Settings.ConnectWisePlatformClientId = clientId;
        Settings.ConnectWisePlatformClientSecret = clientSecret;
        Settings.ConnectWisePlatformScopes = scopes;
        if (!changed) return;
        Settings.ConnectWisePlatformAccessToken = "";
        Settings.ConnectWisePlatformAccessTokenProtected = "";
        Settings.ConnectWisePlatformAccessTokenExpiresAt = null;
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

public sealed class CallNoteWindow : Window
{
    private readonly TextBox _notes = new() { Padding = new Thickness(9), Height = 150, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ComboBox _outcome = new();
    public string Notes => _notes.Text.Trim();
    public string Outcome => _outcome.SelectedItem?.ToString() ?? "";
    public CallNoteWindow(string number, string notes, string outcome)
    {
        Title = "Call notes"; Width = 520; Height = 390; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
        _notes.Text = notes;
        foreach (var item in new[] { "", "Resolved", "Follow-up required", "Escalated", "Ticket created", "No answer" }) _outcome.Items.Add(item);
        _outcome.SelectedItem = _outcome.Items.Cast<string>().FirstOrDefault(x => x == outcome) ?? "";
        var stack = new StackPanel { Margin = new Thickness(26) };
        stack.Children.Add(new TextBlock { Text = $"Notes for {number}", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        stack.Children.Add(new TextBlock { Text = "Outcome", Foreground = new SolidColorBrush(Color.FromRgb(102, 117, 134)), Margin = new Thickness(0, 6, 0, 5) }); stack.Children.Add(_outcome);
        stack.Children.Add(new TextBlock { Text = "Technician notes", Foreground = new SolidColorBrush(Color.FromRgb(102, 117, 134)), Margin = new Thickness(0, 14, 0, 5) }); stack.Children.Add(_notes);
        var save = new Button { Content = "Save notes", Padding = new Thickness(16, 9, 16, 9), HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Color.FromRgb(17, 136, 182)), Foreground = Brushes.White, Margin = new Thickness(0, 16, 0, 0) };
        save.Click += (_, _) => DialogResult = true; stack.Children.Add(save); Content = stack;
    }
}

public sealed class TicketWindow : Window
{
    private readonly TextBox _summary = new() { Padding = new Thickness(9) };
    private readonly TextBox _description = new() { Padding = new Thickness(9), Height = 130, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    public string Summary => _summary.Text.Trim();
    public string Description => _description.Text.Trim();

    public TicketWindow(string company, string contact, string phone)
    {
        Title = "New ConnectWise ticket"; Width = 540; Height = 390; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
        _summary.Text = $"Phone support - {company}";
        _description.Text = $"Caller: {contact}\nPhone: {phone}\n\nIssue and troubleshooting notes:\n";
        var stack = new StackPanel { Margin = new Thickness(26) };
        stack.Children.Add(new TextBlock { Text = "Create service ticket", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        stack.Children.Add(new TextBlock { Text = $"{company} Â· {contact}", Foreground = new SolidColorBrush(Color.FromRgb(102, 117, 134)), Margin = new Thickness(0, 0, 0, 14) });
        AddField(stack, "Summary", _summary); AddField(stack, "Description", _description);
        var create = new Button { Content = "Create ticket", Padding = new Thickness(16, 9, 16, 9), HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Color.FromRgb(17, 136, 182)), Foreground = Brushes.White, Margin = new Thickness(0, 16, 0, 0) };
        create.Click += (_, _) => { if (string.IsNullOrWhiteSpace(Summary)) { MessageBox.Show("Summary is required.", "CallBridge"); return; } DialogResult = true; };
        stack.Children.Add(create); Content = stack;
    }
    private static void AddField(Panel panel, string label, Control field) { panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(102, 117, 134)), Margin = new Thickness(0, 6, 0, 5) }); panel.Children.Add(field); }
}

public sealed class ContactWindow : Window
{
    private readonly TextBox _contact = new() { Padding = new Thickness(9) };
    private readonly TextBox _company = new() { Padding = new Thickness(9) };
    private readonly TextBox _phone = new() { Padding = new Thickness(9) };
    public string ContactName => _contact.Text.Trim();
    public string CompanyName => _company.Text.Trim();
    public string Phone => _phone.Text.Trim();

    public ContactWindow(string contact = "", string company = "", string phone = "")
    {
        Title = string.IsNullOrWhiteSpace(contact) ? "Add contact" : "Edit contact";
        Width = 440; Height = 330; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(246, 248, 251));
        _contact.Text = contact; _company.Text = company; _phone.Text = phone;
        var stack = new StackPanel { Margin = new Thickness(26) };
        stack.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        AddField(stack, "Contact name", _contact); AddField(stack, "Company", _company); AddField(stack, "Phone or extension", _phone);
        var save = new Button { Content = "Save contact", Padding = new Thickness(16, 9, 16, 9), HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Color.FromRgb(17, 136, 182)), Foreground = Brushes.White, Margin = new Thickness(0, 16, 0, 0) };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ContactName) || string.IsNullOrWhiteSpace(CompanyName) || string.IsNullOrWhiteSpace(Phone)) { MessageBox.Show("Contact, company, and phone are required.", "CallBridge"); return; }
            DialogResult = true;
        };
        stack.Children.Add(save); Content = stack;
    }
    private static void AddField(Panel panel, string label, Control field)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(102, 117, 134)), Margin = new Thickness(0, 6, 0, 5) });
        panel.Children.Add(field);
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






