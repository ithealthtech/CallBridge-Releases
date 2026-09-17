using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CallBridge.Desktop;

public partial class MainWindow : Window
{
    private const string Version = "0.14.0";
    private const string StandardSipProvider = "Standard SIP";
    private const string AxionHivePbxProvider = "Axion / HivePBX";
    private const string AxionNoixaPendingProvider = "Axion / Noixa pending";
    // HivePBX (FreePBX-based) feature codes confirmed in the HivePBX control panel: *97 My Voicemail,
    // Default Lot parking extension 4388 with slot 4389 (BLF enabled), pickup by dialing the slot.
    private const string HivePbxVoicemailCode = "*97";
    private const string HivePbxParkCode = "4388";
    private const string HivePbxParkSlots = "4389";
    private const int WmPowerBroadcast = 0x0218;
    private const int PowerResumeAutomatic = 0x0012;
    private const int PowerResumeSuspend = 0x0007;
    private readonly string _settingsPath = GetSettingsPath();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly ProtectedCallEventQueue _callEventQueue;
    private readonly object _callEventTaskSync = new();
    private readonly HashSet<Task> _callEventTasks = [];
    private AppSettings _settings = new();
    private Process? _localServiceProcess;
    private readonly SemaphoreSlim _serviceHealthGate = new(1, 1);
    private string? _localServiceToken;
    private string? _localServicePath;
    private string? _localServiceDatabasePath;
    private int _localServicePort;
    private int _consecutiveServiceHealthFailures;
    private bool _ownsLocalService;
    private bool _serviceRestarting;
    private string? _activeCallId;
    private bool _registered;
    private bool _muted;
    private bool _held;
    private List<RowItem> _currentRows = [];
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly SipSoftphone _sip = new();
    private readonly WindowsRingtonePlayer _ringer = new();
    private string? _sipCallId;
    private UpdateInfo? _availableUpdate;
    private bool _installingUpdate;
    private bool _checkingForUpdates;
    private DateTimeOffset? _lastUpdateCheck;
    private string? _lastUpdateError;
    private SipCallDirection _sipCallDirection = SipCallDirection.None;
    private string _sipRemoteParty = "";
    private string _sipRemoteNumber = "";
    private string _listTitle = RecentCallsTitle;
    private List<RowItem>? _contactRowsCache;
    private bool _suppressDialSearch;
    private bool _serviceReady;
    private bool _serviceBannerShown;
    private bool _settingsSaveWarningShown;
    private bool _loadingSettingsView;
    private bool _settingsDirty;
    private string _activeSettingsSection = "Audio";
    private string? _pendingAdminSection;
    private bool _adminUnlocked;
    private int _adminPinFailures;
    private DateTimeOffset _adminPinRetryAt = DateTimeOffset.MinValue;
    private int _viewRequestVersion;
    private bool _shutdownStarted;
    private volatile bool _registrationRequested;
    private volatile bool _sipRecoveryPending;
    private readonly SemaphoreSlim _sipRecoveryGate = new(1, 1);
    private CancellationTokenSource? _sipRecoveryCts;
    private HwndSource? _windowSource;
    private WindowsIncomingCallNotifier? _incomingCallNotifier;
    private TrayFlyout? _trayFlyout;
    private IncomingCallWindow? _incomingCallWindow;
    private CancellationTokenSource? _callerLookupCts;
    private CancellationTokenSource? _callContextCts;
    private Task<IncomingCallContext>? _callContextTask;
    private IncomingCallContext? _callContext;
    private DateTimeOffset? _callConnectedAt;
    private string _lastSavedNote = "";
    private readonly List<PendingTimeEntry> _pendingTimeEntries = [];
    private PendingTimeEntry? _pendingTimeEntry => _pendingTimeEntries.Count > 0 ? _pendingTimeEntries[0] : null;
    private readonly DispatcherTimer _callTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _exitRequested;
    private bool _trayHintShown;
    private int _backgroundStartupStarted;

    /// <summary>When true, closing the window hides it to the notification area instead of exiting.</summary>
    public bool CloseToTray { get; set; }

    public bool IsPhoneConfigured => HasSipRegistrationSettings();

    private static string GetSettingsPath()
    {
        var isolatedRoot = Environment.GetEnvironmentVariable("CALLBRIDGE_SETTINGS_ROOT");
        var root = string.IsNullOrWhiteSpace(isolatedRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IT Health Technologies", "CallBridge")
            : Path.GetFullPath(isolatedRoot);
        return Path.Combine(root, "settings.json");
    }

    public MainWindow()
    {
        _callEventQueue = new(Path.Combine(Path.GetDirectoryName(_settingsPath)!, "call-events.queue"));
        InitializeComponent();
        ThemePalette.Register(this);
        ThemePalette.Changed += () => { if (!_shutdownStarted) { UpdateProviderStatus(); SetActiveNav(SettingsView.Visibility == Visibility.Visible ? SettingsButton : _listTitle == MoreListTitle ? MoreTabButton : HomeTabButton); } };
        LoadSettings();
        RepairLaunchAtStartupRegistration();
        var localToken = EnsureLocalService();
        if (!string.IsNullOrWhiteSpace(localToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", localToken);
        ApplyTemplates();
        WireEvents();
        InitializeSettingsPage();
        WireCallWorkspace();
        WireTimeEntryDraft();
        WireAdminSettings();
        WireBranding();
        WirePhoneFeatures();
        ShowBrandLogo();
        ApplyBranding();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
        ApplyResponsiveLayout();
        _ = ShowHomeAsync();
        _ = RefreshHealthAsync();
        _statusTimer.Tick += (_, _) =>
        {
            RefreshOperationalStatus();
            _ = RefreshHealthAsync();
            _ = DrainSipEventQueueAsync();
        };
        _statusTimer.Start();
        _sip.RegistrationStateChanged += state => Dispatcher.Invoke(() =>
        {
            _registered = state == "registered";
            UpdateProviderStatus();
        });
        _sip.CallStateChanged += status => Dispatcher.BeginInvoke(() => ApplySipCallStatus(status));
        _sip.TransferStateChanged += status => Dispatcher.BeginInvoke(() => ApplySipTransferStatus(status));
        _sip.CallWaitingStateChanged += status => Dispatcher.BeginInvoke(() => ApplySipCallWaitingStatus(status));
        _sip.CallQualityChanged += snapshot => Dispatcher.BeginInvoke(() => ApplySipCallQuality(snapshot));
        _sip.CallLifecycleChanged += QueueSipLifecycleEvent;
        _sip.AudioStatusChanged += message => Dispatcher.BeginInvoke(() => ShowAudioStatus(message, true));
        _ringer.PlaybackError += message => Dispatcher.BeginInvoke(() => ShowAudioStatus(message, true));
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => StartInBackground();
        Loaded += (_, _) => _ = CheckForUpdatesLoopAsync();
        Closing += OnClosing;
    }

    /// <summary>Starts automatic registration once, whether or not the window has been shown.</summary>
    public async void StartInBackground()
    {
        if (Interlocked.Exchange(ref _backgroundStartupStarted, 1) != 0) return;
        await StartConfiguredRegistrationAsync(showFailure: false);
    }

    /// <summary>Exits the application, bypassing close-to-tray.</summary>
    public void RequestExit()
    {
        _exitRequested = true;
        _trayFlyout?.Close();
        Close();
    }

    public void OpenFromTray(bool showSettings)
    {
        if (_shutdownStarted) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        if (showSettings) ShowSettings();
    }

    public TraySnapshot GetTraySnapshot()
    {
        var status = GetTrayStatus();
        var headset = "Windows default device";
        try { headset = WindowsAudioDeviceCatalog.ResolveMicrophone(_settings.Microphone).Device.DisplayName; }
        catch (Exception ex) { App.LogStartup($"Tray headset lookup warning: {ex.GetType().Name}"); }
        return new TraySnapshot(
            status,
            TrayStatusText(status),
            string.IsNullOrWhiteSpace(_settings.Extension) || !IsPhoneConfigured ? "" : _settings.Extension,
            headset,
            _activeCallId is null ? null : ActiveCallMetric.Text,
            _voicemail.Known ? _voicemail.Summary : null,
            _availableUpdate?.Version.ToString());
    }

    private TrayPhoneStatus GetTrayStatus() =>
        _activeCallId is not null ? TrayPhoneStatus.OnCall
        : _registered ? TrayPhoneStatus.Registered
        : _registrationRequested ? TrayPhoneStatus.Connecting
        : TrayPhoneStatus.Offline;

    private static string TrayStatusText(TrayPhoneStatus status) => status switch
    {
        TrayPhoneStatus.OnCall => "On a call",
        TrayPhoneStatus.Registered => "Registered",
        TrayPhoneStatus.Connecting => "Reconnecting",
        _ => "Offline"
    };

    private void UpdateTrayStatus()
    {
        var status = GetTrayStatus();
        _incomingCallNotifier?.SetStatus(status, $"CallBridge - {TrayStatusText(status)}");
    }

    private void ShowTrayFlyout()
    {
        if (_shutdownStarted) return;
        if (_trayFlyout is not null)
        {
            _trayFlyout.Close();
            return;
        }

        _trayFlyout = new TrayFlyout(
            GetTraySnapshot(),
            openWindow: () => OpenFromTray(showSettings: false),
            openSettings: () => OpenFromTray(showSettings: true),
            quit: RequestExit,
            checkForUpdates: OpenUpdatesFromTray);
        _trayFlyout.Closed += (_, _) => _trayFlyout = null;
        _trayFlyout.Show();
        _trayFlyout.Activate();
    }

    private void ShowIncomingCallWindow(string callerLabel, string number)
    {
        if (_shutdownStarted) return;
        CloseIncomingCallWindow();
        var lookup = new CancellationTokenSource();
        _callerLookupCts = lookup;
        var window = new IncomingCallWindow(
            callerLabel,
            number,
            answer: async () =>
            {
                await AnswerIncomingCallAsync();
                if (_activeCallId is not null) OpenFromTray(showSettings: false);
            },
            decline: DeclineIncomingCallAsync,
            openTicket: OpenConnectWiseTicket);
        window.Closed += (_, _) => { if (ReferenceEquals(_incomingCallWindow, window)) _incomingCallWindow = null; };
        _incomingCallWindow = window;
        window.Bring();
        _ = PopulateIncomingCallWindowAsync(window, number, lookup.Token);
    }

    private async Task PopulateIncomingCallWindowAsync(IncomingCallWindow window, string number, CancellationToken cancellationToken)
    {
        IncomingCallContext context;
        try
        {
            context = await (_callContextTask ?? LookupCallerSafelyAsync(number, cancellationToken));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            App.LogStartup($"Caller lookup warning: {ex.GetType().Name}");
            context = new IncomingCallContext(null, null, null, [], "Caller lookup is unavailable right now.");
        }
        if (!cancellationToken.IsCancellationRequested && ReferenceEquals(_incomingCallWindow, window)) window.SetContext(context);
    }

    private async Task<IncomingCallContext> LookupCallerAsync(string number, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(number))
            return new IncomingCallContext(null, null, null, [], "The caller's number was not provided.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await _http.GetAsync($"{_settings.ApiBase.TrimEnd('/')}/matches?phone={Uri.EscapeDataString(number)}", timeout.Token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        if (!document.RootElement.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array || matches.GetArrayLength() == 0)
            return new IncomingCallContext(null, null, null, [], "This number isn't in the directory. Sync ConnectWise contacts to match callers.");

        var match = matches[0];
        var companyId = Value(match, "companyId");
        var companyName = Value(match, "companyName");
        var contactName = Value(match, "contactName");
        var lastCall = await LastCallSummaryAsync(companyId, companyName, timeout.Token);
        if (!ConnectWiseTicketing.IsConfigured(_settings))
            return new IncomingCallContext(contactName, companyName, companyId, [], "Connect ConnectWise in Settings to see open tickets.", lastCall);
        if (!ConnectWiseClient.IsCompanyId(companyId))
            return new IncomingCallContext(contactName, companyName, companyId, [], "This contact isn't linked to a ConnectWise company.", lastCall);

        try
        {
            using var client = new ConnectWiseTicketing(_settings);
            var (tickets, resolvedCompanyId) = await client.GetOpenTicketsAsync(companyId, companyName, 5, timeout.Token);
            return new IncomingCallContext(contactName, companyName, resolvedCompanyId, tickets, null, lastCall);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or InvalidDataException or JsonException)
        {
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            App.LogStartup($"Open ticket lookup warning: {ex.GetType().Name}");
            return new IncomingCallContext(contactName, companyName, companyId, [], "Open tickets couldn't be loaded from ConnectWise.", lastCall);
        }
    }

    /// <summary>Describes the most recent earlier call with the same company, from local call history.</summary>
    private async Task<string?> LastCallSummaryAsync(string companyId, string companyName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(companyId) && string.IsNullOrWhiteSpace(companyName)) return null;
        try
        {
            using var response = await _http.GetAsync($"{_settings.ApiBase.TrimEnd('/')}/calls/journal?limit=100", cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("calls", out var calls) || calls.ValueKind != JsonValueKind.Array) return null;
            foreach (var call in calls.EnumerateArray())
            {
                var sameCompany = !string.IsNullOrWhiteSpace(companyId)
                    ? Value(call, "companyId") == companyId
                    : string.Equals(Value(call, "companyName"), companyName, StringComparison.OrdinalIgnoreCase);
                if (!sameCompany || !DateTimeOffset.TryParse(Value(call, "startedAt"), out var startedAt)) continue;
                var days = (DateTime.Now.Date - startedAt.ToLocalTime().Date).Days;
                var when = days switch { 0 => "earlier today", 1 => "yesterday", _ => $"{days} days ago" };
                return $"Last call {when} ({startedAt.ToLocalTime():MMM d, h:mm tt}).";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException) { }
        return null;
    }

    private void OpenConnectWiseTicket(string ticketId)
    {
        try
        {
            using var client = new ConnectWiseTicketing(_settings);
            if (client.TicketUrl(ticketId) is not { } url)
            {
                var number = _callContext?.Tickets.FirstOrDefault(ticket => ticket.Id == ticketId)?.DisplayNumber ?? ticketId;
                ShowBanner($"Open ticket #{number} in ConnectWise. The platform API doesn't provide a direct link.", WarnHex);
                return;
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowApiError($"The ticket couldn't be opened: {ex.Message}"); }
    }

    private void CloseIncomingCallWindow()
    {
        _callerLookupCts?.Cancel();
        _callerLookupCts?.Dispose();
        _callerLookupCts = null;
        var window = _incomingCallWindow;
        _incomingCallWindow = null;
        window?.Close();
    }

    private sealed record PendingTimeEntry(string TicketId, string TicketNumber, DateTimeOffset Start, DateTimeOffset End, string Notes);

    private void WireTimeEntryDraft()
    {
        SaveTimeEntryButton.Click += async (_, _) => await SaveTimeEntryAsync();
        DiscardTimeEntryButton.Click += (_, _) => HideTimeEntryDraft();
        TimeEntryMinutesText.TextChanged += (_, _) =>
            SaveTimeEntryButton.IsEnabled = _pendingTimeEntry is not null && int.TryParse(TimeEntryMinutesText.Text, out var minutes) && minutes is > 0 and <= 1440;
        SetControlMetadata(TimeEntryMinutesText, "Minutes to log", "Minutes to log on the ticket. Adjust before saving.");
        SetControlMetadata(SaveTimeEntryButton, "Save time", "Create this time entry in ConnectWise.");
        SetControlMetadata(DiscardTimeEntryButton, "Discard time entry", "Close this draft without logging time.");
    }

    /// <summary>After a connected call with a ticket selected, offers a time entry for the tech to review.</summary>
    private void OfferTimeEntryDraft()
    {
        if (_callConnectedAt is not { } start) return;
        if (CallTicketPicker.SelectedItem is not CallTicketChoice { Id.Length: > 0 } ticket) return;
        if (!ConnectWiseTicketing.IsConfigured(_settings)) return;
        var end = DateTimeOffset.UtcNow;
        var notes = CallNotesText.Text.Trim();
        _pendingTimeEntries.Add(new PendingTimeEntry(ticket.Id, TicketNumber(ticket.Id), start, end, string.IsNullOrWhiteSpace(notes) ? $"Phone call with {CallContactValue.Text}" : notes));
        ShowNextTimeEntryDraft();
    }

    /// <summary>Shows the oldest unsaved time entry. Drafts from back-to-back calls wait in order instead of being dropped.</summary>
    private void ShowNextTimeEntryDraft()
    {
        if (_pendingTimeEntry is not { } entry)
        {
            TimeEntryDraft.Visibility = Visibility.Collapsed;
            return;
        }
        TimeEntryMinutesText.Text = Math.Max(1, (int)Math.Ceiling((entry.End - entry.Start).TotalMinutes)).ToString();
        TimeEntryTargetText.Text = _pendingTimeEntries.Count > 1
            ? $"min on #{entry.TicketNumber} ({_pendingTimeEntries.Count - 1} more waiting)"
            : $"min on #{entry.TicketNumber}";
        var asEntry = ConnectWiseTicketing.RecordsTimeAsEntry(entry.TicketId);
        TimeEntryDetailText.Text = asEntry && string.IsNullOrWhiteSpace(_settings.ConnectWiseMemberIdentifier)
            ? "Add your ConnectWise member ID in Settings > Sign-in to save time."
            : asEntry
                ? $"{entry.Start.ToLocalTime():h:mm tt} call · logged as {_settings.ConnectWiseMemberIdentifier}"
                : $"{entry.Start.ToLocalTime():h:mm tt} call · saved as an internal time note (the platform has no time entries)";
        TimeEntryDraft.Visibility = Visibility.Visible;
        SaveTimeEntryButton.IsEnabled = true;
    }

    private void HideTimeEntryDraft()
    {
        if (_pendingTimeEntries.Count > 0) _pendingTimeEntries.RemoveAt(0);
        ShowNextTimeEntryDraft();
    }

    private async Task SaveTimeEntryAsync()
    {
        if (_pendingTimeEntry is not { } entry) return;
        if (!int.TryParse(TimeEntryMinutesText.Text, out var minutes) || minutes is <= 0 or > 1440)
        {
            TimeEntryDetailText.Text = "Enter between 1 and 1440 minutes.";
            return;
        }
        if (ConnectWiseTicketing.RecordsTimeAsEntry(entry.TicketId) && string.IsNullOrWhiteSpace(_settings.ConnectWiseMemberIdentifier))
        {
            ShowSettings();
            ShowSettingsSection("SignIn");
            SettingsCwMemberText.Focus();
            return;
        }

        SetBusyAction(SaveTimeEntryButton, "Saving time to ConnectWise...");
        try
        {
            using var client = new ConnectWiseTicketing(_settings);
            await client.RecordTimeAsync(entry.TicketId, _settings.ConnectWiseMemberIdentifier, entry.Start, entry.Start.AddMinutes(minutes), entry.Notes);
            HideTimeEntryDraft();
            ShowBanner($"Logged {minutes} min on ticket #{entry.TicketNumber}.", GoodHex);
        }
        catch (Exception ex)
        {
            TimeEntryDetailText.Text = $"Time wasn't saved: {ex.Message}";
        }
        finally
        {
            RestoreAction(SaveTimeEntryButton, "Create this time entry in ConnectWise.");
        }
    }

    private void WireCallWorkspace()
    {
        _callTimer.Tick += (_, _) => UpdateCallTimer();
        BlindTransferButton.Click += async (_, _) => await ExecuteTransferAsync(consultFirst: false);
        ConsultTransferButton.Click += async (_, _) => await ExecuteTransferAsync(consultFirst: true);
        CloseTransferButton.Click += (_, _) => TransferPanel.Visibility = Visibility.Collapsed;
        TransferTargetText.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; TransferPanel.Visibility = Visibility.Collapsed; }
            else if (e.Key == Key.Enter) { e.Handled = true; await ExecuteTransferAsync(consultFirst: false); }
        };
        OpenCallTicketButton.Click += (_, _) =>
        {
            if (CallTicketPicker.SelectedItem is CallTicketChoice { Id.Length: > 0 } choice) OpenConnectWiseTicket(choice.Id);
        };
        NewCallTicketButton.Click += async (_, _) => await CreateTicketForCallAsync();
        SaveCallNoteButton.Click += async (_, _) => await SaveCallNoteAsync();
        CallTicketPicker.SelectionChanged += (_, _) => UpdateCallNoteState();
        CallNotesText.TextChanged += (_, _) =>
        {
            CallNotesPlaceholder.Visibility = string.IsNullOrEmpty(CallNotesText.Text) ? Visibility.Visible : Visibility.Collapsed;
            UpdateCallNoteState();
        };

        SetControlMetadata(TransferTargetText, "Transfer destination", "Extension or phone number to transfer the caller to.");
        SetControlMetadata(BlindTransferButton, "Blind transfer", "Transfer the caller immediately.");
        SetControlMetadata(ConsultTransferButton, "Consult first", "Call the destination first, then complete or cancel the transfer.");
        SetControlMetadata(CloseTransferButton, "Close transfer", "Hide the transfer options.");
        SetControlMetadata(CallTicketPicker, "Log to ticket", "ConnectWise ticket that call notes are saved to.");
        SetControlMetadata(OpenCallTicketButton, "Open ticket in ConnectWise", "Open the selected ticket in ConnectWise.");
        SetControlMetadata(NewCallTicketButton, "New ticket", "Create a ConnectWise ticket for this caller's company.");
        SetControlMetadata(CallNotesText, "Call notes", "Notes for this call. They are saved to the selected ticket.");
        SetControlMetadata(SaveCallNoteButton, "Save note now", "Save these notes to the selected ConnectWise ticket as an internal note.");
    }

    /// <summary>Resets the in-call workspace for a new call and starts the caller lookup it shares with the screen pop.</summary>
    private void BeginCallContext(string callerLabel, string number)
    {
        _callContextCts?.Cancel();
        _callContextCts?.Dispose();
        var source = new CancellationTokenSource();
        _callContextCts = source;
        _callContext = null;
        _lastSavedNote = "";
        _callConnectedAt = null;
        _callTimer.Stop();
        CallTimerText.Text = "00:00";
        CallCompanyText.Text = "";
        CallCompanyValue.Text = "—";
        CallContactValue.Text = string.IsNullOrWhiteSpace(callerLabel) ? "Unknown caller" : callerLabel;
        CallNumberValue.Text = string.IsNullOrWhiteSpace(number) ? "—" : number;
        CallTicketsPanel.Children.Clear();
        CallTicketsNote.Text = "Looking up the caller…";
        CallTicketsNote.Visibility = Visibility.Visible;
        CallQualityText.Text = "Waiting for call-quality data";
        CallTicketPicker.ItemsSource = new List<CallTicketChoice> { CallTicketChoice.None };
        CallTicketPicker.SelectedIndex = 0;
        CallNotesText.Clear();
        TransferPanel.Visibility = Visibility.Collapsed;
        TransferTargetText.Clear();
        UpdateCallNoteState();
        _callContextTask = LookupCallerSafelyAsync(number, source.Token);
        _ = ApplyCallContextWhenReadyAsync(_callContextTask, source.Token);
    }

    private async Task<IncomingCallContext> LookupCallerSafelyAsync(string number, CancellationToken cancellationToken)
    {
        try
        {
            return await LookupCallerAsync(number, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.LogStartup($"Caller lookup warning: {ex.GetType().Name}");
            return new IncomingCallContext(null, null, null, [], "Caller lookup is unavailable right now.");
        }
    }

    private async Task ApplyCallContextWhenReadyAsync(Task<IncomingCallContext> lookup, CancellationToken cancellationToken)
    {
        try
        {
            var context = await lookup;
            if (!cancellationToken.IsCancellationRequested) ApplyCallContext(context);
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyCallContext(IncomingCallContext context, string? selectTicketId = null)
    {
        var chosenTicketId = selectTicketId ?? (CallTicketPicker.SelectedItem as CallTicketChoice)?.Id ?? "";
        _callContext = context;
        if (!string.IsNullOrWhiteSpace(context.ContactName))
        {
            CallContactValue.Text = context.ContactName;
            ActiveCallMetric.Text = context.ContactName;
        }
        CallCompanyValue.Text = string.IsNullOrWhiteSpace(context.CompanyName) ? "Not matched" : context.CompanyName;
        CallCompanyText.Text = context.CompanyName ?? "";
        CallTicketsPanel.Children.Clear();
        foreach (var ticket in context.Tickets)
            CallTicketsPanel.Children.Add(CallTicketRow(ticket));
        CallTicketsNote.Text = context.Tickets.Count == 0 ? context.Message ?? "No open tickets for this company." : context.Message ?? context.LastCall ?? "";
        CallTicketsNote.Visibility = string.IsNullOrWhiteSpace(CallTicketsNote.Text) ? Visibility.Collapsed : Visibility.Visible;

        // No ticket is the default so notes and time only reach a ticket the technician chose.
        var choices = new List<CallTicketChoice> { CallTicketChoice.None };
        choices.AddRange(context.Tickets.Select(ticket => new CallTicketChoice(ticket.Id, $"#{ticket.DisplayNumber} · {ticket.Summary}")));
        CallTicketPicker.ItemsSource = choices;
        CallTicketPicker.SelectedItem = choices.FirstOrDefault(choice => choice.Id == chosenTicketId) ?? CallTicketChoice.None;
        UpdateCallNoteState();
    }

    private FrameworkElement CallTicketRow(ConnectWiseTicketSummary ticket)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = $"#{ticket.DisplayNumber}", FontSize = 12, Foreground = Brush(MutedHex), FontFamily = (FontFamily)FindResource("MonoFont"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        var summary = new TextBlock { Text = string.IsNullOrWhiteSpace(ticket.Summary) ? "(no summary)" : ticket.Summary, FontWeight = FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = ticket.Summary };
        Grid.SetColumn(summary, 1);
        row.Children.Add(summary);
        var priority = System.Text.RegularExpressions.Regex.Match(ticket.Priority ?? "", @"Priority\s*(\d)");
        var pill = IncomingCallWindow.Pill(priority.Success ? $"P{priority.Groups[1].Value}" : "Open", IncomingCallWindow.PriorityBrush(ticket.Priority));
        pill.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(pill, 2);
        row.Children.Add(pill);
        var button = new Button
        {
            Content = row,
            Style = (Style)FindResource("GhostButton"),
            Foreground = (Brush)FindResource("Ink"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(2, 6, 2, 6),
            MinHeight = 0,
            ToolTip = $"Open ticket #{ticket.DisplayNumber} in ConnectWise"
        };
        AutomationProperties.SetName(button, $"Ticket {ticket.DisplayNumber}: {ticket.Summary}");
        button.Click += (_, _) => OpenConnectWiseTicket(ticket.Id);
        return new Border { BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Child = button };
    }

    private void UpdateCallTimer()
    {
        if (_callConnectedAt is not { } connectedAt) return;
        var elapsed = DateTimeOffset.UtcNow - connectedAt;
        CallTimerText.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    private void MarkCallConnected()
    {
        if (_callConnectedAt is not null) return;
        _callConnectedAt = DateTimeOffset.UtcNow;
        UpdateCallTimer();
        _callTimer.Start();
    }

    private void UpdateCallNoteState()
    {
        var ticket = CallTicketPicker.SelectedItem as CallTicketChoice;
        var hasTicket = ticket is { Id.Length: > 0 };
        var note = CallNotesText.Text.Trim();
        OpenCallTicketButton.IsEnabled = hasTicket;
        NewCallTicketButton.IsEnabled = true;
        SaveCallNoteButton.IsEnabled = hasTicket && note.Length > 0 && note != _lastSavedNote;
        if (note.Length > 0 && note == _lastSavedNote) return;
        CallNoteStatusText.Text = !hasTicket
            ? "Choose a ticket to save notes to ConnectWise."
            : note.Length == 0
                ? $"Notes are saved to #{TicketNumber(ticket!.Id)} as an internal note when you save or hang up."
                : $"Unsaved notes for #{TicketNumber(ticket!.Id)}.";
    }

    private async Task SaveCallNoteAsync()
    {
        if (CallTicketPicker.SelectedItem is not CallTicketChoice { Id.Length: > 0 } ticket) return;
        var note = CallNotesText.Text.Trim();
        if (note.Length == 0) return;
        SetBusyAction(SaveCallNoteButton, "Saving note to ConnectWise...");
        try
        {
            await AddTicketNoteAsync(ticket.Id, note);
            _lastSavedNote = note;
            CallNoteStatusText.Text = $"Saved as an internal note on #{TicketNumber(ticket.Id)} at {DateTime.Now:h:mm tt}.";
        }
        catch (Exception ex)
        {
            CallNoteStatusText.Text = $"The note couldn't be saved: {ex.Message}";
        }
        finally
        {
            RestoreAction(SaveCallNoteButton, "Save these notes to the selected ConnectWise ticket as an internal note.");
            UpdateCallNoteState();
        }
    }

    private async Task AddTicketNoteAsync(string ticketId, string note)
    {
        if (!ConnectWiseTicketing.IsConfigured(_settings)) throw new InvalidOperationException("Connect ConnectWise in Settings first.");
        using var client = new ConnectWiseTicketing(_settings);
        await client.AddTicketNoteAsync(ticketId, note);
    }

    /// <summary>The number techs recognize for a ticket in the current call (platform ticket IDs are UUIDs).</summary>
    private string TicketNumber(string ticketId) =>
        _callContext?.Tickets.FirstOrDefault(ticket => ticket.Id == ticketId)?.DisplayNumber
        ?? (ConnectWisePlatformClient.IsPlatformId(ticketId) ? ticketId[..8] : ticketId);

    /// <summary>Keeps notes from being lost when a call ends: saves them to the chosen ticket, or copies them to the clipboard.</summary>
    private void PreserveCallNotesOnEnd()
    {
        var note = CallNotesText.Text.Trim();
        if (note.Length == 0 || note == _lastSavedNote) return;
        if (CallTicketPicker.SelectedItem is CallTicketChoice { Id.Length: > 0 } ticket && ConnectWiseTicketing.IsConfigured(_settings))
        {
            _ = SaveNotesAfterCallAsync(ticket.Id, note);
            return;
        }
        try
        {
            Clipboard.SetText(note);
            ShowBanner("Call notes weren't linked to a ticket, so they were copied to your clipboard.", WarnHex);
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            ShowBanner("Call notes weren't linked to a ticket and couldn't be copied to the clipboard.", WarnHex);
        }
    }

    private async Task SaveNotesAfterCallAsync(string ticketId, string note)
    {
        try
        {
            await AddTicketNoteAsync(ticketId, note);
            ShowBanner($"Call notes were saved to ticket #{TicketNumber(ticketId)}.", GoodHex);
        }
        catch (Exception ex)
        {
            try { Clipboard.SetText(note); } catch (Exception clipboardError) when (clipboardError is COMException or ExternalException) { }
            ShowBanner($"Call notes couldn't be saved to #{TicketNumber(ticketId)} ({ex.Message}). They were copied to your clipboard.", WarnHex);
        }
    }

    private void ResetCallWorkspace()
    {
        _callContextCts?.Cancel();
        _callContextCts?.Dispose();
        _callContextCts = null;
        _callContextTask = null;
        _callContext = null;
        _callConnectedAt = null;
        _callTimer.Stop();
        CallNotesText.Clear();
        _lastSavedNote = "";
        TransferPanel.Visibility = Visibility.Collapsed;
        CallCompanyText.Text = "";
    }

    private async Task CreateTicketForCallAsync()
    {
        var context = _callContext ?? new IncomingCallContext(null, null, null, [], null);
        if (ConnectWiseTicketing.TicketCreationProblem(_settings) is { } problem) { ShowWarning(problem); return; }
        var editor = new TicketWindow(context.CompanyName ?? "", context.ContactName ?? CallContactValue.Text, CallNumberValue.Text, context.CompanyId ?? "", SearchCompaniesAsync) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try
        {
            using var client = new ConnectWiseTicketing(_settings);
            var summary = await client.CreateTicketAsync(editor.CompanyId, editor.CompanyName, editor.Summary, editor.Description);
            var ticketId = summary.Id;
            var sameCompany = context.CompanyId == editor.CompanyId;
            ApplyCallContext(context with
            {
                CompanyId = editor.CompanyId,
                CompanyName = string.IsNullOrWhiteSpace(editor.CompanyName) ? context.CompanyName : editor.CompanyName,
                Tickets = sameCompany ? [summary, .. context.Tickets] : [summary],
                Message = null
            }, selectTicketId: ticketId);
            CallNoteStatusText.Text = $"Created ticket #{summary.DisplayNumber}. Notes will be saved to it.";
        }
        catch (Exception ex) { ShowWarning($"Ticket creation failed: {ex.Message}"); }
    }

    private async Task<IReadOnlyList<ConnectWiseCompanySummary>> SearchCompaniesAsync(string query)
    {
        using var client = new ConnectWiseTicketing(_settings);
        return await client.SearchCompaniesAsync(query);
    }

    private bool _keypadOpen;
    private const double TwoColumnMinWidth = 760;

    /// <summary>
    /// Arranges Home. Idle and wide: dial card and status cards on the left, the list on the right.
    /// In a call: call bar and workspace across the full width. More or narrow windows: one column.
    /// </summary>
    private void ApplyHomeLayout()
    {
        var sipStatus = _sip.CurrentCall;
        var pendingIncoming = sipStatus.HasPendingIncomingCall || _sip.CurrentWaitingCall.HasPendingCall;
        var inCall = (_activeCallId is not null || _sipCallId is not null || sipStatus.State is not (SipCallState.Idle or SipCallState.Failed)) && !pendingIncoming;
        var more = _listTitle == MoreListTitle;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var twoColumns = !inCall && !more && width >= TwoColumnMinWidth;

        CallWorkspace.Visibility = inCall ? Visibility.Visible : Visibility.Collapsed;
        ListSection.Visibility = inCall ? Visibility.Collapsed : Visibility.Visible;
        PhoneStrip.Visibility = inCall || !more ? Visibility.Visible : Visibility.Collapsed;
        StatusCards.Visibility = twoColumns ? Visibility.Visible : Visibility.Collapsed;
        CallSideColumn.Width = new GridLength(width < 700 ? 200 : 260);

        HomeLeftColumn.Width = twoColumns ? new GridLength(292) : new GridLength(0);
        HomeGapColumn.Width = twoColumns ? new GridLength(14) : new GridLength(0);
        HomeGapRow.Height = new GridLength(twoColumns || more ? 0 : 12);

        // Dial card: left column when two-column, otherwise a full-width strip on top.
        Grid.SetRow(PhoneStrip, 0);
        Grid.SetColumn(PhoneStrip, twoColumns ? 0 : 0);
        Grid.SetColumnSpan(PhoneStrip, twoColumns ? 1 : 3);
        Grid.SetRow(StatusCards, 2);
        Grid.SetColumn(StatusCards, 0);
        Grid.SetRow(ListSection, twoColumns || more ? 0 : 2);
        Grid.SetRowSpan(ListSection, twoColumns || more ? 3 : 1);
        Grid.SetColumn(ListSection, twoColumns ? 2 : 0);
        Grid.SetColumnSpan(ListSection, twoColumns ? 1 : 3);
        StatusCards.Margin = new Thickness(0, twoColumns ? 12 : 0, 0, 0);
        HomeGapRow.Height = new GridLength(0);

        // Call row: stacked search box over a full-width Call button in the left column; one row otherwise.
        var stacked = twoColumns;
        Grid.SetColumnSpan(DialHost, stacked ? 4 : 1);
        Grid.SetRow(CallButton, stacked ? 1 : 0);
        Grid.SetColumn(CallButton, stacked ? 0 : 3);
        Grid.SetColumnSpan(CallButton, stacked ? 4 : 1);
        CallButton.Margin = stacked ? new Thickness(0, 8, 0, 0) : new Thickness(6, 0, 0, 0);
        CallButton.MinHeight = stacked ? 40 : 36;
        KeypadToggleButton.Visibility = stacked ? Visibility.Collapsed : Visibility.Visible;
        DialPad.Visibility = stacked || _keypadOpen ? Visibility.Visible : Visibility.Collapsed;
        ListSection.Margin = new Thickness(0, !twoColumns && !more && !inCall ? 12 : 0, 0, 0);
    }

    private void UpdateStatusCards()
    {
        PhoneCardStatusText.Text = PresenceText.Text;
        PhoneCardDot.Fill = PresenceDot.Fill;
        PhoneCardDetailText.Text = string.IsNullOrWhiteSpace(_settings.Extension) ? "" : $"Ext {_settings.Extension}";

        var voicemailCode = _settings.VoicemailAccessCode.Trim();
        VoicemailCardCountText.Text = _voicemail.Known ? _voicemail.NewMessages.ToString() : "–";
        VoicemailCardCountText.SetResourceReference(TextBlock.ForegroundProperty, _voicemail.NewMessages > 0 ? "Accent" : "Ink");
        VoicemailCardDetailText.Text = !SipFeatureCodes.IsValidDialString(voicemailCode)
            ? "Set the access code in Settings"
            : !_voicemail.Known ? "Waiting for status" : _voicemail.NewMessages > 0 ? $"new · {_voicemail.SavedMessages} saved" : $"No new · {_voicemail.SavedMessages} saved";
        VoicemailCardCallButton.IsEnabled = SipFeatureCodes.IsValidDialString(voicemailCode);

        ParkCardSlots.Children.Clear();
        var prefix = _settings.ParkPickupPrefix.Trim();
        foreach (var slot in _parkSlots)
        {
            var state = _parkStates.TryGetValue(slot, out var known) ? known : ParkSlotState.Unknown;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var pickup = new Button { Content = "Pick up", Style = (Style)FindResource("ToolbarButton"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = state != ParkSlotState.Open };
            SetControlMetadata(pickup, $"Pick up slot {slot}", $"Dial {prefix}{slot} to pick up a call parked on {slot}.");
            pickup.Click += async (_, _) => await DialDestinationAsync(prefix + slot);
            DockPanel.SetDock(pickup, Dock.Right);
            row.Children.Add(pickup);
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, state switch { ParkSlotState.Occupied => "Warn", ParkSlotState.Open => "Good", _ => "Muted" });
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock
            {
                Text = state switch { ParkSlotState.Occupied => $"{slot} · call parked", ParkSlotState.Open => $"{slot} · open", _ => $"{slot}" },
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            ParkCardSlots.Children.Add(row);
        }
        ParkCardEmptyText.Visibility = _parkSlots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task DialDestinationAsync(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || _activeCallId is not null) return;
        _suppressDialSearch = true;
        try { DialText.Text = destination; }
        finally { _suppressDialSearch = false; }
        await ToggleCallAsync();
    }

    /// <summary>Handles the per-row buttons in the recent calls and contact lists.</summary>
    private async void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button { Tag: string action, DataContext: RowItem row }) return;
        e.Handled = true;
        RowsList.SelectedItem = row;
        switch (action)
        {
            case "Primary": await ActivateSelectedRowAsync(RowsList); break;
            case "OpenCompany": OpenSelectedCompany(); break;
            case "NewTicket": await CreateTicketForSelectedContactAsync(); break;
            case "Notes": await EditSelectedCallNoteAsync(); break;
            case "Edit": await EditSelectedContactAsync(); break;
            case "Delete": await DeleteSelectedContactAsync(); break;
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownStarted) return;

        if (CloseToTray && !_exitRequested)
        {
            e.Cancel = true;
            // Hiding to the tray ends the admin session so the next person to open Settings needs the PIN.
            LockAdminSettings();
            Hide();
            if (!_trayHintShown && _activeCallId is null)
            {
                _trayHintShown = true;
                _incomingCallNotifier?.ShowInformation("CallBridge is still running", "Incoming calls will still ring. Right-click the tray icon to quit.");
            }
            return;
        }

        e.Cancel = true;
        _trayFlyout?.Close();
        CloseIncomingCallWindow();
        _shutdownStarted = true;
        _registrationRequested = false;
        CancelSipRecovery();
        _ringer.Stop();
        _incomingCallNotifier?.Clear();
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        if (_windowSource is not null) _windowSource.RemoveHook(WindowMessageHook);
        IsEnabled = false;
        _statusTimer.Stop();

        try { await _sip.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { App.LogStartup($"SIP shutdown warning: {ex.GetType().Name}"); }

        try
        {
            await AwaitPendingCallEventsAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await DrainSipEventQueueAsync();
        }
        catch (Exception ex) { App.LogStartup($"Call history shutdown warning: {ex.GetType().Name}"); }

        try { await StopOwnedLocalServiceAsync(); }
        catch (Exception ex) { App.LogStartup($"Service shutdown warning: {ex.GetType().Name}"); }
        finally
        {
            _ringer.Dispose();
            _incomingCallNotifier?.Dispose();
            _localServiceProcess?.Dispose();
            _http.Dispose();
        }

        App.LogStartup("Desktop shutdown complete.");
        Application.Current.Shutdown();
    }

    private string? EnsureLocalService()
    {
        _localServicePort = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var configuredPort) && configuredPort is >= 1024 and <= 65535
            ? configuredPort
            : 8787;
        _settings.ApiBase = $"http://127.0.0.1:{_localServicePort}";
        var localToken = Environment.GetEnvironmentVariable("CALLBRIDGE_LOCAL_TOKEN");
        if (!string.IsNullOrWhiteSpace(localToken))
        {
            _localServiceToken = localToken;
            App.LogStartup("Using an externally managed local service.");
            return localToken;
        }

        _localServicePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "service", "CallBridge.Service.exe"));
        if (!File.Exists(_localServicePath))
        {
            App.LogStartup("The packaged local service executable was not found.");
            return null;
        }

        _localServiceToken = NewSessionToken();
        var runtimeRoot = Path.GetDirectoryName(_settingsPath)!;
        var dataDir = Path.Combine(runtimeRoot, "data");
        Directory.CreateDirectory(dataDir);
        _localServiceDatabasePath = Path.Combine(dataDir, "callbridge.db");
        _ownsLocalService = true;
        StartOwnedLocalService();
        return _localServiceToken;
    }

    private void StartOwnedLocalService()
    {
        if (!_ownsLocalService
            || string.IsNullOrWhiteSpace(_localServicePath)
            || string.IsNullOrWhiteSpace(_localServiceToken)
            || string.IsNullOrWhiteSpace(_localServiceDatabasePath))
            throw new InvalidOperationException("The desktop-owned local service is not configured.");

        var startInfo = new ProcessStartInfo(_localServicePath)
        {
            WorkingDirectory = Path.GetDirectoryName(_localServicePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["CALLBRIDGE_LOCAL_TOKEN"] = _localServiceToken;
        startInfo.Environment["CALLBRIDGE_CALL_RETENTION_DAYS"] = "90";
        startInfo.Environment["PORT"] = _localServicePort.ToString();
        startInfo.Environment["DATABASE_PATH"] = _localServiceDatabasePath;

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.Exited += (_, _) =>
        {
            if (_shutdownStarted || _serviceRestarting) return;
            App.LogStartup("The local service exited unexpectedly; recovery was requested.");
            if (!Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() => _ = RefreshHealthAsync(forceRecovery: true));
        };

        _localServiceProcess = process;
        if (!process.Start()) throw new InvalidOperationException("The local service could not be started.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        App.LogStartup($"Local service started (process {process.Id}).");
    }

    private async Task StopOwnedLocalServiceAsync()
    {
        if (!_ownsLocalService || _localServiceProcess is null) return;
        _serviceRestarting = true;
        try
        {
            if (!_localServiceProcess.HasExited)
            {
                _localServiceProcess.Kill(true);
                await _localServiceProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            _localServiceProcess.Dispose();
            _localServiceProcess = null;
            _serviceRestarting = false;
        }
    }

    private static string NewSessionToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    private void WireEvents()
    {
        RegisterButton.Click += async (_, _) => await ToggleRegistrationAsync();
        CallButton.Click += async (_, _) => await ToggleCallAsync();
        AnswerCallButton.Click += async (_, _) => await AnswerIncomingCallAsync();
        DeclineCallButton.Click += async (_, _) => await DeclineIncomingCallAsync();
        MuteButton.Click += async (_, _) => await ToggleMuteAsync();
        HoldButton.Click += async (_, _) => await ToggleHoldAsync();
        TransferButton.Click += async (_, _) => await TransferAsync();
        DtmfButton.Click += async (_, _) =>
        {
            if (_sip.CurrentTransfer.CanCancel) await CancelAttendedTransferAsync();
            else await SendDtmfAsync();
        };
        DialText.TextChanged += async (_, _) =>
        {
            DialPlaceholder.Visibility = string.IsNullOrEmpty(DialText.Text) ? Visibility.Visible : Visibility.Collapsed;
            UpdatePhoneControlState();
            await SearchFromDialTextAsync();
        };
        DialText.KeyDown += async (_, e) => await HandleDialTextKeyDownAsync(e);
        KeypadToggleButton.Click += (_, _) => ToggleKeypad();
        StatusBannerDismissButton.Click += (_, _) => { StatusBanner.Visibility = Visibility.Collapsed; InstallUpdateButton.Visibility = Visibility.Collapsed; };
        InstallUpdateButton.Click += async (_, _) => await InstallUpdateAsync();
        ImportContactsButton.Click += async (_, _) => await ImportContactsAsync();
        SyncConnectWiseButton.Click += async (_, _) => await SyncConnectWiseAsync();
        CreateTicketButton.Click += async (_, _) => await CreateTicketForSelectedContactAsync();
        ExportHistoryButton.Click += async (_, _) => await ExportCallHistoryAsync();
        AddContactButton.Click += async (_, _) => await AddContactAsync();
        RowsList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnRowActionClick));
        VoicemailCardCallButton.Click += async (_, _) => await DialDestinationAsync(_settings.VoicemailAccessCode.Trim());
        ClearContactsButton.Click += async (_, _) => await ClearImportedContactsAsync();
        RefreshRowsButton.Click += async (_, _) => await RefreshCurrentViewAsync();
        RowsList.MouseDoubleClick += async (_, _) => await ActivateSelectedRowAsync(RowsList);
        RowsList.KeyDown += async (_, e) => await ActivateListRowOnEnterAsync(RowsList, e);
        RowsList.SelectionChanged += (_, _) => UpdateListActionState();

        HomeTabButton.Click += async (_, _) => await ShowHomeAsync();
        MoreTabButton.Click += (_, _) => ShowRows(MoreListTitle, MoreSubtitle, MoreRows());
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

    private void InitializeSettingsPage()
    {
        foreach (var provider in new[] { StandardSipProvider, AxionHivePbxProvider, AxionNoixaPendingProvider })
            SettingsProviderBox.Items.Add(provider);
        foreach (var transport in SipTransportProfile.DisplayNames) SettingsSipTransportBox.Items.Add(transport);
        foreach (var codecProfile in SipCodecProfile.DisplayNames) SettingsSipCodecProfileBox.Items.Add(codecProfile);
        foreach (var region in new[] { ConnectWisePlatformClient.NorthAmericaBaseUrl, ConnectWisePlatformClient.EuropeBaseUrl, ConnectWisePlatformClient.AustraliaBaseUrl })
            SettingsCwPlatformBaseBox.Items.Add(region);

        SettingsGeneralTabButton.Click += (_, _) => ShowSettingsSection("General");
        SettingsSipTabButton.Click += (_, _) => ShowSettingsSection("SIP");
        SettingsConnectWiseTabButton.Click += (_, _) => ShowSettingsSection("ConnectWise");
        SettingsAppTabButton.Click += (_, _) => ShowSettingsSection("App");
        SettingsSaveButton.Click += async (_, _) => await SaveSettingsPageAsync();
        SettingsDiscardButton.Click += (_, _) =>
        {
            LoadSettingsIntoView();
            SetSettingsStatus("Changes discarded. Saved settings are shown.", "neutral");
        };
        SettingsTestPlatformButton.Click += async (_, _) => await TestSettingsPlatformAsync();
        SettingsLoadPlatformBoardsButton.Click += async (_, _) => await LoadPlatformBoardsAsync();
        SettingsCwModeBox.ItemsSource = ConnectWiseTicketingMode.Choices.Select(choice => new { choice.Value, choice.Label }).ToList();
        SettingsCwModeBox.SelectionChanged += (_, _) => UpdateConnectWiseModeHelp();
        SettingsTestPsaButton.Click += async (_, _) => await TestSettingsPsaAsync();
        SettingsRefreshAudioButton.Click += (_, _) => RefreshAudioDevices(true);
        SettingsTestMicrophoneButton.Click += async (_, _) => await TestMicrophoneAsync();
        SettingsTestSpeakerButton.Click += async (_, _) => await TestSpeakerAsync();
        SettingsTestRingtoneButton.Click += async (_, _) => await TestRingtoneAsync();
        SettingsCreateSupportBundleButton.Click += async (_, _) => await CreateSupportBundleAsync();
        SettingsProviderBox.SelectionChanged += (_, _) =>
        {
            if (_loadingSettingsView) return;
            if (SettingsProviderBox.SelectedItem?.ToString() == AxionHivePbxProvider)
            {
                SettingsSipTransportBox.SelectedItem = "UDP";
                SettingsSipCodecProfileBox.SelectedItem = SipCodecProfile.AxionName;
                // Fill HivePBX voicemail and parking defaults only where the admin hasn't entered values.
                if (string.IsNullOrWhiteSpace(SettingsVoicemailCodeText.Text)) SettingsVoicemailCodeText.Text = HivePbxVoicemailCode;
                if (string.IsNullOrWhiteSpace(SettingsParkCodeText.Text)) SettingsParkCodeText.Text = HivePbxParkCode;
                if (string.IsNullOrWhiteSpace(SettingsParkSlotsText.Text)) SettingsParkSlotsText.Text = HivePbxParkSlots;
            }
        };

        foreach (var field in new[]
        {
            SettingsExtensionText, SettingsCallerIdText, SettingsVoicemailCodeText, SettingsParkSlotsText, SettingsParkCodeText, SettingsParkPickupPrefixText, SettingsSipServerText,
            SettingsSipUsernameText, SettingsCwPlatformClientIdText, SettingsCwPlatformScopesText,
            SettingsCwSiteText, SettingsCwCompanyText, SettingsCwPublicText, SettingsCwClientIdText,
            SettingsCwBoardIdText, SettingsCwMemberText, SettingsProductNameText, SettingsCompanyNameText
        }) field.TextChanged += (_, _) => MarkSettingsDirty();
        foreach (var field in new[] { SettingsSipPassword, SettingsCwPlatformSecret, SettingsCwPrivateKey })
            field.PasswordChanged += (_, _) => MarkSettingsDirty();
        foreach (var field in new[] { SettingsProviderBox, SettingsSipTransportBox, SettingsSipCodecProfileBox, SettingsCwPlatformBaseBox, SettingsMicrophoneBox, SettingsSpeakerBox, SettingsRingtoneSpeakerBox })
            field.SelectionChanged += (_, _) => MarkSettingsDirty();
        SettingsStartupCheckBox.Checked += (_, _) => MarkSettingsDirty();
        SettingsStartupCheckBox.Unchecked += (_, _) => MarkSettingsDirty();
        SettingsTopmostCheckBox.Checked += (_, _) => MarkSettingsDirty();
        SettingsTopmostCheckBox.Unchecked += (_, _) => MarkSettingsDirty();

        foreach (FrameworkElement element in new FrameworkElement[]
        {
            SettingsGeneralTabButton, SettingsSipTabButton, SettingsConnectWiseTabButton, SettingsAppTabButton,
            SettingsTestPlatformButton, SettingsTestPsaButton, SettingsSaveButton, SettingsDiscardButton,
            SettingsProviderBox, SettingsExtensionText, SettingsCallerIdText, SettingsVoicemailCodeText, SettingsParkSlotsText, SettingsParkCodeText, SettingsParkPickupPrefixText, SettingsSipServerText,
            SettingsSipUsernameText, SettingsSipPassword, SettingsSipTransportBox, SettingsSipCodecProfileBox, SettingsMicrophoneBox, SettingsSpeakerBox, SettingsRingtoneSpeakerBox,
            SettingsRefreshAudioButton, SettingsTestMicrophoneButton, SettingsTestSpeakerButton, SettingsTestRingtoneButton, SettingsCwPlatformBaseBox,
            SettingsCwPlatformClientIdText, SettingsCwPlatformSecret, SettingsCwPlatformScopesText, SettingsCwSiteText,
            SettingsCwCompanyText, SettingsCwPublicText, SettingsCwPrivateKey, SettingsCwClientIdText, SettingsCwBoardIdText, SettingsCwMemberText,
            SettingsProductNameText, SettingsCompanyNameText, SettingsAccentText, SettingsStartupCheckBox, SettingsTopmostCheckBox
        }) ToolTipService.SetShowOnDisabled(element, true);

        LoadSettingsIntoView();
        ShowSettingsSection(_activeSettingsSection);
    }

    private void LoadSettingsIntoView()
    {
        _loadingSettingsView = true;
        try
        {
            SettingsProviderBox.SelectedItem = _settings.Provider;
            if (SettingsProviderBox.SelectedItem is null) SettingsProviderBox.SelectedItem = StandardSipProvider;
            SettingsExtensionText.Text = _settings.Extension;
            SettingsCallerIdText.Text = _settings.CallerId;
            SettingsVoicemailCodeText.Text = _settings.VoicemailAccessCode;
            SettingsParkSlotsText.Text = _settings.ParkSlots;
            SettingsParkCodeText.Text = _settings.ParkCode;
            SettingsParkPickupPrefixText.Text = _settings.ParkPickupPrefix;
            SettingsSipServerText.Text = _settings.SipServer;
            SettingsSipUsernameText.Text = _settings.SipUsername;
            SettingsSipPassword.Password = _settings.SipPassword;
            SettingsSipTransportBox.SelectedItem = SipTransportProfile.DisplayNames.Contains(_settings.SipTransport, StringComparer.OrdinalIgnoreCase)
                ? SipTransportProfile.DisplayNames.First(item => item.Equals(_settings.SipTransport, StringComparison.OrdinalIgnoreCase))
                : "UDP";
            SettingsSipCodecProfileBox.SelectedItem = SipCodecProfile.Resolve(_settings.SipCodecProfileName).Name;
            RefreshAudioDevices(false);
            SettingsCwPlatformBaseBox.Text = _settings.ConnectWisePlatformBaseUrl;
            SettingsCwPlatformClientIdText.Text = _settings.ConnectWisePlatformClientId;
            SettingsCwPlatformSecret.Password = _settings.ConnectWisePlatformClientSecret;
            SettingsCwPlatformScopesText.Text = _settings.ConnectWisePlatformScopes;
            SettingsCwModeBox.SelectedValue = ConnectWiseTicketingMode.Normalize(_settings.ConnectWiseTicketingMode);
            SetPlatformLookupChoices(SettingsCwPlatformBoardBox, [], _settings.ConnectWisePlatformBoardId, _settings.ConnectWisePlatformBoardName);
            SetPlatformLookupChoices(SettingsCwPlatformSourceBox, [], _settings.ConnectWisePlatformSourceId, _settings.ConnectWisePlatformSourceName);
            UpdateConnectWiseModeHelp();
            SettingsCwSiteText.Text = _settings.ConnectWiseSite;
            SettingsCwCompanyText.Text = _settings.ConnectWiseCompanyId;
            SettingsCwPublicText.Text = _settings.ConnectWisePublicKey;
            SettingsCwPrivateKey.Password = _settings.ConnectWisePrivateKey;
            SettingsCwClientIdText.Text = _settings.ConnectWiseClientId;
            SettingsCwBoardIdText.Text = _settings.ConnectWiseBoardId == 0 ? "" : _settings.ConnectWiseBoardId.ToString();
            SettingsCwMemberText.Text = _settings.ConnectWiseMemberIdentifier;
            SettingsProductNameText.Text = _settings.ProductName;
            SettingsAccentText.Text = _settings.BrandAccentColor;
            SettingsCompanyNameText.Text = _settings.CompanyName;
            SettingsStartupCheckBox.IsChecked = _settings.LaunchAtStartup;
            SettingsTopmostCheckBox.IsChecked = _settings.AlwaysOnTopDuringCalls;
            _settingsDirty = false;
            SettingsSaveButton.IsEnabled = false;
            SettingsDiscardButton.IsEnabled = false;
        }
        finally
        {
            _loadingSettingsView = false;
        }
    }

    private void RefreshAudioDevices(bool announce)
    {
        var microphoneSelection = announce
            ? SettingsMicrophoneBox.SelectedValue?.ToString() ?? _settings.Microphone
            : _settings.Microphone;
        var speakerSelection = announce
            ? SettingsSpeakerBox.SelectedValue?.ToString() ?? _settings.Speaker
            : _settings.Speaker;
        var ringtoneSelection = announce
            ? SettingsRingtoneSpeakerBox.SelectedValue?.ToString() ?? _settings.RingtoneSpeaker
            : _settings.RingtoneSpeaker;
        var microphones = WindowsAudioDeviceCatalog.GetMicrophones();
        var speakers = WindowsAudioDeviceCatalog.GetSpeakers();
        var wasLoading = _loadingSettingsView;
        _loadingSettingsView = true;
        WindowsAudioDeviceResolution microphone;
        WindowsAudioDeviceResolution speaker;
        WindowsAudioDeviceResolution ringtone;
        try
        {
            SettingsMicrophoneBox.ItemsSource = microphones;
            SettingsSpeakerBox.ItemsSource = speakers;
            SettingsRingtoneSpeakerBox.ItemsSource = speakers;
            microphone = WindowsAudioDeviceCatalog.Resolve(microphones, microphoneSelection, "microphone");
            speaker = WindowsAudioDeviceCatalog.Resolve(speakers, speakerSelection, "speaker");
            ringtone = WindowsAudioDeviceCatalog.Resolve(speakers, ringtoneSelection, "ringtone speaker");
            SettingsMicrophoneBox.SelectedValue = microphone.Device.Id;
            SettingsSpeakerBox.SelectedValue = speaker.Device.Id;
            SettingsRingtoneSpeakerBox.SelectedValue = ringtone.Device.Id;
        }
        finally
        {
            _loadingSettingsView = wasLoading;
        }

        var warning = microphone.UserMessage ?? speaker.UserMessage ?? ringtone.UserMessage;
        ShowAudioStatus(warning ?? $"Found {Math.Max(0, microphones.Count - 1)} microphone(s) and {Math.Max(0, speakers.Count - 1)} speaker(s).", warning is not null);
        if (announce && warning is null) SetSettingsStatus("Audio device list refreshed", "success");
    }

    private async Task TestMicrophoneAsync()
    {
        if (_sip.CurrentCall.State is not (SipCallState.Idle or SipCallState.Failed))
        {
            ShowAudioStatus("End the current call before testing the microphone.", true);
            return;
        }

        SettingsTestMicrophoneButton.IsEnabled = false;
        ShowAudioStatus("Listening to the microphone...", false);
        try
        {
            var result = await WindowsAudioDeviceTester.TestMicrophoneAsync(SettingsMicrophoneBox.SelectedValue?.ToString());
            ShowAudioStatus(result.Message, !result.Success);
        }
        finally
        {
            SettingsTestMicrophoneButton.IsEnabled = true;
        }
    }

    private async Task TestSpeakerAsync()
    {
        if (_sip.CurrentCall.State is not (SipCallState.Idle or SipCallState.Failed))
        {
            ShowAudioStatus("End the current call before testing the speaker.", true);
            return;
        }

        SettingsTestSpeakerButton.IsEnabled = false;
        ShowAudioStatus("Playing a speaker test tone...", false);
        try
        {
            var result = await WindowsAudioDeviceTester.TestSpeakerAsync(SettingsSpeakerBox.SelectedValue?.ToString());
            ShowAudioStatus(result.Message, !result.Success);
        }
        finally
        {
            SettingsTestSpeakerButton.IsEnabled = true;
        }
    }

    private async Task TestRingtoneAsync()
    {
        if (_sip.CurrentCall.State is not (SipCallState.Idle or SipCallState.Failed))
        {
            ShowAudioStatus("End the current call before testing the ringtone.", true);
            return;
        }

        SettingsTestRingtoneButton.IsEnabled = false;
        ShowAudioStatus("Playing a ringtone test...", false);
        try
        {
            var result = await WindowsAudioDeviceTester.TestSpeakerAsync(SettingsRingtoneSpeakerBox.SelectedValue?.ToString());
            ShowAudioStatus(result.Success ? "Ringtone test played. Confirm that you heard the tone." : result.Message, !result.Success);
        }
        finally
        {
            SettingsTestRingtoneButton.IsEnabled = true;
        }
    }

    private void ShowAudioStatus(string message, bool warning)
    {
        SettingsAudioStatusText.Text = message;
        SettingsAudioStatusText.Foreground = warning ? Brush(WarnHex) : Brush(MutedHex);
        if (warning && SettingsView.Visibility == Visibility.Visible) SetSettingsStatus(message, "warning");
    }

    private void MarkSettingsDirty()
    {
        if (_loadingSettingsView) return;
        _settingsDirty = true;
        SettingsSaveButton.IsEnabled = true;
        SettingsDiscardButton.IsEnabled = true;
        SetSettingsStatus("Unsaved changes", "warning");
    }

    private static readonly HashSet<string> AdminSettingsSections = ["General", "SIP", "ConnectWise", "App"];

    private bool HasAdminPin => !string.IsNullOrWhiteSpace(_settings.AdminPinHash);

    private bool AdminSettingsLocked => HasAdminPin && !_adminUnlocked;

    private static readonly (string Name, string Hex)[] AccentPresets =
    [
        ("Blue", "#1F6FD1"), ("Teal", "#0E7C74"), ("Purple", "#7A3FC1"), ("Orange", "#C2410C")
    ];

    private void WireBranding()
    {
        foreach (var (name, hex) in AccentPresets)
        {
            var swatch = new Button
            {
                Width = 28,
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 4, 6, 0),
                Background = UiPalette.Brush(hex),
                BorderBrush = UiPalette.Brush(hex),
                ToolTip = $"{name} {hex}"
            };
            AutomationProperties.SetName(swatch, $"{name} accent");
            swatch.Click += (_, _) => SettingsAccentText.Text = hex;
            AccentPresetsPanel.Children.Add(swatch);
        }
        SettingsAccentText.TextChanged += (_, _) =>
        {
            MarkSettingsDirty();
            PreviewAccent();
        };
        ResetAccentButton.Click += (_, _) => SettingsAccentText.Text = "";
        ChooseLogoButton.Click += (_, _) => ChooseLogo();
        RemoveLogoButton.Click += (_, _) => RemoveLogo();
        Branding.Changed += () =>
        {
            _incomingCallNotifier?.ResetIcons();
            UpdateTrayStatus();
            ShowBrandLogo();
        };
    }

    private void PreviewAccent()
    {
        if (Branding.TryNormalizeAccent(SettingsAccentText.Text, out var normalized, out var message))
        {
            AccentPreview.Background = UiPalette.Brush(normalized);
            AccentMessageText.Text = string.IsNullOrWhiteSpace(SettingsAccentText.Text)
                ? "Using the default CallBridge blue. Save settings to apply."
                : $"{normalized} will be used for buttons, tabs, highlights, and the logo tile after you save.";
            AccentMessageText.Foreground = Brush(MutedHex);
        }
        else
        {
            AccentMessageText.Text = message;
            AccentMessageText.Foreground = Brush(BadHex);
        }
    }

    private void ChooseLogo()
    {
        var picker = new OpenFileDialog { Filter = "Logo images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg", Title = "Choose a logo" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            _settings.BrandLogoPath = Branding.ImportLogo(picker.FileName);
            LogoMessageText.Text = SaveSettings(true) ? "Logo updated." : "The logo was imported but settings couldn't be saved.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            LogoMessageText.Text = ex.Message;
        }
    }

    private void RemoveLogo()
    {
        try { Branding.RemoveLogo(_settings.BrandLogoPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.LogStartup($"Branding logo removal warning: {ex.GetType().Name}"); }
        _settings.BrandLogoPath = "";
        LogoMessageText.Text = SaveSettings(true) ? "Logo removed. The product initials are shown instead." : "Settings couldn't be saved.";
    }

    private void ShowBrandLogo()
    {
        var logo = Branding.Logo;
        foreach (var (tile, image, text) in new[] { (LogoPreviewTile, LogoPreviewImage, LogoPreviewText) })
        {
            image.Source = logo;
            image.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
            text.Text = Branding.ProductInitials;
            text.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
            if (logo is null) tile.SetResourceReference(Border.BackgroundProperty, "Accent");
            else tile.Background = Brushes.Transparent;
        }
        // An uploaded logo replaces the tile and product name in the header; without one, show both.
        HeaderLogoImage.Source = logo;
        HeaderLogoImage.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;
        LogoTile.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
        BrandTitle.Visibility = logo is null ? Visibility.Visible : Visibility.Collapsed;
        LogoText.Text = Branding.ProductInitials;
        LogoPreviewTile.Width = logo is null ? 48 : double.NaN;
        LogoPreviewTile.MaxWidth = 200;        RemoveLogoButton.IsEnabled = logo is not null;
    }

    private void WireAdminSettings()
    {
        SettingsAudioTabButton.Click += (_, _) => ShowSettingsSection("Audio");
        SettingsAboutTabButton.Click += (_, _) => ShowSettingsSection("About");
        CheckForUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync(manual: true);
        AboutInstallUpdateButton.Click += async (_, _) => await InstallUpdateAsync();
        UpdateBadgeButton.Click += async (_, _) => await InstallUpdateAsync();
        ReleaseNotesButton.Click += (_, _) => OpenReleaseNotes();
        RefreshUpdateIndicators();
        SettingsSignInTabButton.Click += (_, _) => ShowSettingsSection("SignIn");
        SettingsAdminTabButton.Click += (_, _) => ShowSettingsSection(_pendingAdminSection ?? "General");
        SettingsLockButton.Click += (_, _) => LockAdminSettings();
        UnlockAdminButton.Click += (_, _) => UnlockAdminSettings();
        AdminPinEntryBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            UnlockAdminSettings();
        };
        SetAdminPinButton.Click += (_, _) => SetAdminPin();
        RemoveAdminPinButton.Click += (_, _) => RemoveAdminPin();
        UpdateAdminPinState();
    }

    private void ShowSettingsSection(string section)
    {
        if (AdminSettingsSections.Contains(section) && AdminSettingsLocked)
        {
            _pendingAdminSection = section;
            section = "Locked";
        }
        _activeSettingsSection = section == "Locked" ? _pendingAdminSection ?? "General" : section;

        SettingsAudioSection.Visibility = section == "Audio" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSignInSection.Visibility = section == "SignIn" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAboutSection.Visibility = section == "About" ? Visibility.Visible : Visibility.Collapsed;
        SettingsLockedSection.Visibility = section == "Locked" ? Visibility.Visible : Visibility.Collapsed;
        SettingsGeneralSection.Visibility = section == "General" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSipSection.Visibility = section == "SIP" ? Visibility.Visible : Visibility.Collapsed;
        SettingsConnectWiseSection.Visibility = section == "ConnectWise" ? Visibility.Visible : Visibility.Collapsed;
        SettingsAppSection.Visibility = section == "App" ? Visibility.Visible : Visibility.Collapsed;

        SettingsAdminTabButton.Visibility = AdminSettingsLocked ? Visibility.Visible : Visibility.Collapsed;
        AdminTabsPanel.Visibility = AdminSettingsLocked ? Visibility.Collapsed : Visibility.Visible;
        SettingsLockButton.Visibility = HasAdminPin && _adminUnlocked ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (button, name) in new[]
        {
            (SettingsAudioTabButton, "Audio"),
            (SettingsSignInTabButton, "SignIn"),
            (SettingsAboutTabButton, "About"),
            (SettingsAdminTabButton, "Locked"),
            (SettingsGeneralTabButton, "General"),
            (SettingsSipTabButton, "SIP"),
            (SettingsConnectWiseTabButton, "ConnectWise"),
            (SettingsAppTabButton, "App")
        })
        {
            button.Tag = name == section ? ActiveTabTag : null;
        }

        if (section == "Locked")
        {
            AdminPinEntryBox.Clear();
            AdminPinMessageText.Text = RetryMessage();
            AdminPinEntryBox.Focus();
        }
        SettingsScroller.ScrollToTop();
    }

    private void UnlockAdminSettings()
    {
        var wait = _adminPinRetryAt - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            AdminPinMessageText.Text = RetryMessage();
            return;
        }

        if (AdminPin.Verify(AdminPinEntryBox.Password, _settings.AdminPinHash, _settings.AdminPinSalt))
        {
            _adminUnlocked = true;
            _adminPinFailures = 0;
            AdminPinEntryBox.Clear();
            AdminPinMessageText.Text = "";
            App.LogStartup("Admin settings unlocked.");
            ShowSettingsSection(_pendingAdminSection ?? "General");
            return;
        }

        _adminPinFailures++;
        _adminPinRetryAt = DateTimeOffset.UtcNow + AdminPin.RetryDelay(_adminPinFailures);
        App.LogStartup("Admin PIN attempt failed.");
        AdminPinEntryBox.Clear();
        AdminPinMessageText.Text = _adminPinRetryAt > DateTimeOffset.UtcNow ? RetryMessage() : "That PIN isn't correct. Try again.";
        AdminPinEntryBox.Focus();
    }

    private string RetryMessage()
    {
        var wait = _adminPinRetryAt - DateTimeOffset.UtcNow;
        return wait > TimeSpan.Zero
            ? $"Too many incorrect PINs. Try again in {Math.Ceiling(wait.TotalSeconds)} seconds."
            : "";
    }

    private void LockAdminSettings()
    {
        _adminUnlocked = false;
        _pendingAdminSection = null;
        if (SettingsView.Visibility == Visibility.Visible) ShowSettingsSection("Audio");
    }

    private void SetAdminPin()
    {
        if (AdminSettingsLocked) return;
        var pin = AdminPinNewBox.Password;
        if (!AdminPin.IsValidFormat(pin))
        {
            AdminPinSetMessageText.Text = "Use 4 to 12 digits.";
            AdminPinNewBox.Focus();
            return;
        }
        if (pin != AdminPinConfirmBox.Password)
        {
            AdminPinSetMessageText.Text = "The PINs don't match.";
            AdminPinConfirmBox.Focus();
            return;
        }

        var (hash, salt) = AdminPin.Create(pin);
        _settings.AdminPinHash = hash;
        _settings.AdminPinSalt = salt;
        _adminUnlocked = true;
        AdminPinNewBox.Clear();
        AdminPinConfirmBox.Clear();
        AdminPinSetMessageText.Text = SaveSettings(false)
            ? "Admin PIN saved. Admin settings lock when you leave Settings or select Lock."
            : "The PIN couldn't be saved to settings.";
        App.LogStartup("Admin PIN set.");
        UpdateAdminPinState();
        ShowSettingsSection("App");
    }

    private void RemoveAdminPin()
    {
        if (AdminSettingsLocked || !HasAdminPin) return;
        if (!ConfirmWarning("Remove the admin PIN? Anyone using this computer will be able to change SIP, ConnectWise, and branding settings.")) return;
        _settings.AdminPinHash = "";
        _settings.AdminPinSalt = "";
        AdminPinSetMessageText.Text = SaveSettings(false) ? "Admin PIN removed." : "The change couldn't be saved to settings.";
        App.LogStartup("Admin PIN removed.");
        UpdateAdminPinState();
        ShowSettingsSection("App");
    }

    private void UpdateAdminPinState()
    {
        AdminPinStatusText.Text = HasAdminPin
            ? "An admin PIN is set. Admin settings require it after you leave Settings or select Lock."
            : "No admin PIN is set. Anyone using this computer can change admin settings.";
        SetAdminPinButton.Content = HasAdminPin ? "Change PIN" : "Set PIN";
        RemoveAdminPinButton.Visibility = HasAdminPin ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSignInStatus()
    {
        SignInStatusText.Text = PresenceText.Text;
        SignInStatusDot.Fill = PresenceDot.Fill;
        SignInExtensionText.Text = string.IsNullOrWhiteSpace(_settings.Extension) ? "" : $"Extension {_settings.Extension}";
    }

    private void SetSettingsStatus(string message, string state)
    {
        SettingsStatusText.Text = message;
        (SettingsStatusText.Foreground, SettingsStatusDot.Fill) = state switch
        {
            "success" => (Brush(GoodHex), Brush(GoodHex)),
            "warning" => (Brush(WarnHex), Brush(WarnHex)),
            "error" => (Brush(BadHex), Brush(BadHex)),
            _ => (Brush(MutedHex), Brush(MutedHex))
        };
    }

    private bool TryReadSettingsView(out AppSettings candidate)
    {
        candidate = CloneSettings(_settings);
        if (string.IsNullOrWhiteSpace(SettingsExtensionText.Text))
        {
            ShowSettingsSection("General");
            SettingsExtensionText.Focus();
            SetSettingsStatus("Extension is required.", "error");
            return false;
        }
        if (!string.IsNullOrWhiteSpace(SettingsCwBoardIdText.Text) && !int.TryParse(SettingsCwBoardIdText.Text, out _))
        {
            ShowSettingsSection("ConnectWise");
            SettingsCwBoardIdText.Focus();
            SetSettingsStatus("Default service board ID must be a whole number.", "error");
            return false;
        }

        var platformBase = SettingsCwPlatformBaseBox.Text.Trim();
        var platformClientId = SettingsCwPlatformClientIdText.Text.Trim();
        var platformSecret = SettingsCwPlatformSecret.Password;
        var platformScopes = SettingsCwPlatformScopesText.Text.Trim();
        var platformChanged = !string.Equals(candidate.ConnectWisePlatformBaseUrl, platformBase, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.ConnectWisePlatformClientId, platformClientId, StringComparison.Ordinal)
            || !string.Equals(candidate.ConnectWisePlatformClientSecret, platformSecret, StringComparison.Ordinal)
            || !string.Equals(candidate.ConnectWisePlatformScopes, platformScopes, StringComparison.Ordinal);

        candidate.Extension = SettingsExtensionText.Text.Trim();
        candidate.CallerId = SettingsCallerIdText.Text.Trim();
        foreach (var (field, label) in new[] { (SettingsVoicemailCodeText, "Voicemail access code"), (SettingsParkCodeText, "Park code"), (SettingsParkPickupPrefixText, "Pickup prefix") })
        {
            if (string.IsNullOrWhiteSpace(field.Text) || SipFeatureCodes.IsValidDialString(field.Text)) continue;
            ShowSettingsSection("General");
            field.Focus();
            SetSettingsStatus($"{label} can only contain digits, *, #, and +.", "error");
            return false;
        }
        if (!SipFeatureCodes.TryParseSlots(SettingsParkSlotsText.Text, out _, out var slotsMessage))
        {
            ShowSettingsSection("General");
            SettingsParkSlotsText.Focus();
            SetSettingsStatus(slotsMessage, "error");
            return false;
        }
        candidate.VoicemailAccessCode = SettingsVoicemailCodeText.Text.Trim();
        candidate.ParkSlots = SettingsParkSlotsText.Text.Trim();
        candidate.ParkCode = SettingsParkCodeText.Text.Trim();
        candidate.ParkPickupPrefix = SettingsParkPickupPrefixText.Text.Trim();
        candidate.Provider = SettingsProviderBox.SelectedItem?.ToString() ?? StandardSipProvider;
        candidate.SipServer = SettingsSipServerText.Text.Trim();
        candidate.SipUsername = SettingsSipUsernameText.Text.Trim();
        candidate.SipPassword = SettingsSipPassword.Password;
        candidate.SipTransport = SettingsSipTransportBox.SelectedItem?.ToString() ?? "UDP";
        candidate.SipTransport = candidate.Provider == AxionHivePbxProvider ? "UDP" : candidate.SipTransport;
        candidate.SipCodecProfileName = candidate.Provider == AxionHivePbxProvider
            ? SipCodecProfile.AxionName
            : SipCodecProfile.Resolve(SettingsSipCodecProfileBox.SelectedItem?.ToString()).Name;
        candidate.Microphone = SettingsMicrophoneBox.SelectedValue?.ToString() ?? WindowsAudioDeviceCatalog.DefaultDeviceId;
        candidate.Speaker = SettingsSpeakerBox.SelectedValue?.ToString() ?? WindowsAudioDeviceCatalog.DefaultDeviceId;
        candidate.RingtoneSpeaker = SettingsRingtoneSpeakerBox.SelectedValue?.ToString() ?? WindowsAudioDeviceCatalog.DefaultDeviceId;
        candidate.ConnectWisePlatformBaseUrl = platformBase;
        candidate.ConnectWisePlatformClientId = platformClientId;
        candidate.ConnectWisePlatformClientSecret = platformSecret;
        candidate.ConnectWisePlatformScopes = platformScopes;
        candidate.ConnectWiseTicketingMode = ConnectWiseTicketingMode.Normalize(SettingsCwModeBox.SelectedValue as string);
        if (SettingsCwPlatformBoardBox.SelectedItem is ConnectWisePlatformClient.PlatformLookup board)
        {
            candidate.ConnectWisePlatformBoardId = board.Id;
            candidate.ConnectWisePlatformBoardName = board.Name;
        }
        if (SettingsCwPlatformSourceBox.SelectedItem is ConnectWisePlatformClient.PlatformLookup source)
        {
            candidate.ConnectWisePlatformSourceId = source.Id;
            candidate.ConnectWisePlatformSourceName = source.Name;
        }
        candidate.ConnectWiseSite = SettingsCwSiteText.Text.Trim();
        candidate.ConnectWiseCompanyId = SettingsCwCompanyText.Text.Trim();
        candidate.ConnectWisePublicKey = SettingsCwPublicText.Text.Trim();
        candidate.ConnectWisePrivateKey = SettingsCwPrivateKey.Password;
        candidate.ConnectWiseClientId = SettingsCwClientIdText.Text.Trim();
        candidate.ConnectWiseBoardId = int.TryParse(SettingsCwBoardIdText.Text, out var boardId) ? boardId : 0;
        candidate.ConnectWiseMemberIdentifier = SettingsCwMemberText.Text.Trim();
        if (!Branding.TryNormalizeAccent(SettingsAccentText.Text, out var accent, out var accentMessage))
        {
            ShowSettingsSection("App");
            SettingsAccentText.Focus();
            SetSettingsStatus(accentMessage, "error");
            return false;
        }
        candidate.BrandAccentColor = string.IsNullOrWhiteSpace(SettingsAccentText.Text) ? "" : accent;
        candidate.ProductName = string.IsNullOrWhiteSpace(SettingsProductNameText.Text) ? "CallBridge" : SettingsProductNameText.Text.Trim();
        candidate.CompanyName = string.IsNullOrWhiteSpace(SettingsCompanyNameText.Text) ? "IT Health Technologies" : SettingsCompanyNameText.Text.Trim();
        candidate.LaunchAtStartup = SettingsStartupCheckBox.IsChecked == true;
        candidate.AlwaysOnTopDuringCalls = SettingsTopmostCheckBox.IsChecked == true;
        if (platformChanged)
        {
            candidate.ConnectWisePlatformAccessToken = "";
            candidate.ConnectWisePlatformAccessTokenProtected = "";
            candidate.ConnectWisePlatformAccessTokenExpiresAt = null;
        }
        return true;
    }

    private async Task SaveSettingsPageAsync()
    {
        if (!TryReadSettingsView(out var candidate)) return;
        SettingsSaveButton.IsEnabled = false;
        SetSettingsStatus("Saving settings...", "neutral");
        var previousSettings = _settings;
        var previousProvider = _settings.Provider;
        var previousLaunchAtStartup = _settings.LaunchAtStartup;
        var sipConnectionChanged = !string.Equals(previousSettings.SipServer, candidate.SipServer, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previousSettings.SipUsername, candidate.SipUsername, StringComparison.Ordinal)
            || !string.Equals(previousSettings.SipPassword, candidate.SipPassword, StringComparison.Ordinal)
            || !string.Equals(previousSettings.SipTransport, candidate.SipTransport, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(previousSettings.Provider, candidate.Provider, StringComparison.Ordinal);
        try
        {
            SetLaunchAtStartup(candidate.LaunchAtStartup);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException or InvalidOperationException or ArgumentException)
        {
            SettingsSaveButton.IsEnabled = true;
            SetSettingsStatus($"Windows startup could not be updated: {ex.Message}", "error");
            return;
        }
        _settings = candidate;
        _sip.ConfigureAudioDevices(_settings.Microphone, _settings.Speaker);
        _sip.ConfigureCodecProfile(_settings.SipCodecProfileName);
        var saved = SaveSettings(true);
        if (!saved)
        {
            try { SetLaunchAtStartup(previousLaunchAtStartup); } catch { }
            _settings = previousSettings;
            _sip.ConfigureAudioDevices(_settings.Microphone, _settings.Speaker);
            _sip.ConfigureCodecProfile(_settings.SipCodecProfileName);
            ApplyBranding();
            SettingsSaveButton.IsEnabled = true;
            SetSettingsStatus("Settings could not be written. Check access to the CallBridge settings folder.", "error");
            return;
        }
        if (!string.Equals(previousProvider, _settings.Provider, StringComparison.Ordinal) && !IsSipBackedProvider(_settings.Provider) && (_sip.IsRegistered || _registrationRequested))
        {
            _registrationRequested = false;
            CancelSipRecovery();
            await _sip.UnregisterAsync();
        }
        else if (sipConnectionChanged && (_sip.IsRegistered || _registrationRequested))
        {
            _registrationRequested = false;
            CancelSipRecovery();
            await _sip.UnregisterAsync();
            _registered = false;
        }
        if (sipConnectionChanged && _settings.RegistrationEnabled)
            await StartConfiguredRegistrationAsync(showFailure: true);
        _settingsDirty = false;
        SettingsDiscardButton.IsEnabled = false;
        SetSettingsStatus(sipConnectionChanged ? "Settings saved. Register the phone to use the updated SIP connection." : "Settings saved", "success");
        UpdateProviderStatus();
    }

    private async Task TestSettingsPsaAsync()
    {
        var candidate = new AppSettings
        {
            ConnectWiseSite = SettingsCwSiteText.Text.Trim(),
            ConnectWiseCompanyId = SettingsCwCompanyText.Text.Trim(),
            ConnectWisePublicKey = SettingsCwPublicText.Text.Trim(),
            ConnectWisePrivateKey = SettingsCwPrivateKey.Password,
            ConnectWiseClientId = SettingsCwClientIdText.Text.Trim()
        };
        if (!ConnectWiseClient.IsConfigured(candidate))
        {
            SetSettingsStatus("Enter Site URL, Company ID, Public Key, Private Key, and Client ID.", "error");
            return;
        }
        SetBusyAction(SettingsTestPsaButton, "Testing ConnectWise PSA credentials...");
        SetSettingsStatus("Testing ConnectWise PSA...", "neutral");
        try
        {
            using var client = new ConnectWiseClient(candidate);
            var result = await client.TestAsync();
            SetSettingsStatus(result.Message, result.Success ? "success" : "error");
        }
        catch (Exception ex) { SetSettingsStatus(ex.Message, "error"); }
        finally { RestoreAction(SettingsTestPsaButton, "Test ConnectWise PSA API member credentials"); }
    }

    private void UpdateConnectWiseModeHelp()
    {
        SettingsCwModeHelpText.Text = ConnectWiseTicketingMode.Normalize(SettingsCwModeBox.SelectedValue as string) switch
        {
            ConnectWiseTicketingMode.Platform => "Tickets and notes use ConnectWise Platform. Callers match on each company's main number and primary contact, plus contacts you import into CallBridge.",
            ConnectWiseTicketingMode.PlatformWithPsa => "Tickets and notes use ConnectWise Platform. The contact directory syncs from PSA for full caller matching; PSA companies are matched to Platform by their linked ID or exact name.",
            _ => "Everything uses the ConnectWise PSA API member below, including time entries."
        };
    }

    private static void SetPlatformLookupChoices(ComboBox box, IReadOnlyList<ConnectWisePlatformClient.PlatformLookup> items, string selectedId, string selectedName)
    {
        var list = items.ToList();
        if (ConnectWisePlatformClient.IsPlatformId(selectedId) && list.All(item => !string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
            list.Insert(0, new ConnectWisePlatformClient.PlatformLookup(selectedId, string.IsNullOrWhiteSpace(selectedName) ? "Saved selection" : selectedName));
        box.ItemsSource = list;
        box.SelectedItem = list.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadPlatformBoardsAsync()
    {
        if (!TryReadSettingsView(out var candidate)) return;
        if (!ConnectWisePlatformClient.IsConfigured(candidate))
        {
            SetSettingsStatus("Enter the Platform API URL, OAuth Client ID, Client Secret, and scopes first.", "error");
            return;
        }
        SetBusyAction(SettingsLoadPlatformBoardsButton, "Loading service boards and sources...");
        SetSettingsStatus("Loading service boards and sources from ConnectWise Platform...", "neutral");
        try
        {
            using var client = new ConnectWisePlatformClient(candidate);
            var boards = await client.GetServiceBoardsAsync();
            var sources = await client.GetSourcesAsync();
            var boardId = (SettingsCwPlatformBoardBox.SelectedItem as ConnectWisePlatformClient.PlatformLookup)?.Id ?? candidate.ConnectWisePlatformBoardId;
            var sourceId = (SettingsCwPlatformSourceBox.SelectedItem as ConnectWisePlatformClient.PlatformLookup)?.Id
                ?? (ConnectWisePlatformClient.IsPlatformId(candidate.ConnectWisePlatformSourceId) ? candidate.ConnectWisePlatformSourceId
                    : sources.FirstOrDefault(source => source.Name.Contains("phone", StringComparison.OrdinalIgnoreCase))?.Id ?? "");
            SetPlatformLookupChoices(SettingsCwPlatformBoardBox, boards, boardId, candidate.ConnectWisePlatformBoardName);
            SetPlatformLookupChoices(SettingsCwPlatformSourceBox, sources, sourceId, candidate.ConnectWisePlatformSourceName);
            SetSettingsStatus($"Loaded {boards.Count} service boards and {sources.Count} sources. Choose the defaults, then save.", "success");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            SetSettingsStatus($"Boards couldn't be loaded: {ex.Message}", "error");
        }
        finally { RestoreAction(SettingsLoadPlatformBoardsButton, "Load service boards and sources from ConnectWise Platform"); }
    }

    private async Task TestSettingsPlatformAsync()
    {
        if (!TryReadSettingsView(out var candidate)) return;
        if (!ConnectWisePlatformClient.IsConfigured(candidate))
        {
            SetSettingsStatus("Enter the Platform API URL, OAuth Client ID, Client Secret, and scopes.", "error");
            return;
        }
        SetBusyAction(SettingsTestPlatformButton, "Testing ConnectWise Platform OAuth credentials...");
        SetSettingsStatus("Testing ConnectWise Platform OAuth...", "neutral");
        try
        {
            using var client = new ConnectWisePlatformClient(candidate);
            var result = await client.TestAsync();
            SetSettingsStatus(result.Message, result.Success ? "success" : "error");
        }
        catch (Exception ex) { SetSettingsStatus(ex.Message, "error"); }
        finally { RestoreAction(SettingsTestPlatformButton, "Test ConnectWise Platform OAuth credentials"); }
    }

    private async Task CreateSupportBundleAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save CallBridge support bundle",
            Filter = "ZIP archive (*.zip)|*.zip",
            AddExtension = true,
            DefaultExt = ".zip",
            FileName = $"CallBridge-Support-{DateTime.Now:yyyyMMdd-HHmm}.zip"
        };
        if (dialog.ShowDialog(this) != true) return;

        SetBusyAction(SettingsCreateSupportBundleButton, "Creating sanitized support bundle...");
        SetSettingsStatus("Creating support bundle...", "neutral");
        try
        {
            var snapshot = new SupportBundleSnapshot(
                Version,
                _serviceReady,
                _sip.IsRegistered,
                _settings.Provider,
                _settings.SipTransport,
                _settings.SipCodecProfileName,
                HasSipRegistrationSettings(),
                ConnectWiseClient.IsConfigured(_settings),
                ConnectWisePlatformClient.IsConfigured(_settings),
                _settings.LaunchAtStartup,
                _settings.RegistrationEnabled,
                Math.Max(0, SettingsMicrophoneBox.Items.Count - 1),
                Math.Max(0, SettingsSpeakerBox.Items.Count - 1),
                App.ReadStartupLogTail());
            await SupportBundleBuilder.CreateAsync(dialog.FileName, snapshot);
            SetSettingsStatus("Support bundle created", "success");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            SetSettingsStatus($"Support bundle could not be created: {ex.Message}", "error");
        }
        finally
        {
            RestoreAction(SettingsCreateSupportBundleButton, "Create a sanitized ZIP for CallBridge support");
        }
    }

    private static AppSettings CloneSettings(AppSettings settings) => new()
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
        SipCodecProfileName = settings.SipCodecProfileName,
        Microphone = settings.Microphone,
        Speaker = settings.Speaker,
        RingtoneSpeaker = settings.RingtoneSpeaker,
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
        ConnectWiseTicketingMode = settings.ConnectWiseTicketingMode,
        ConnectWisePlatformBoardId = settings.ConnectWisePlatformBoardId,
        ConnectWisePlatformBoardName = settings.ConnectWisePlatformBoardName,
        ConnectWisePlatformSourceId = settings.ConnectWisePlatformSourceId,
        ConnectWisePlatformSourceName = settings.ConnectWisePlatformSourceName,
        ConnectWiseMemberIdentifier = settings.ConnectWiseMemberIdentifier,
        AdminPinHash = settings.AdminPinHash,
        AdminPinSalt = settings.AdminPinSalt,
        BrandAccentColor = settings.BrandAccentColor,
        BrandLogoPath = settings.BrandLogoPath,
        VoicemailAccessCode = settings.VoicemailAccessCode,
        ParkSlots = settings.ParkSlots,
        ParkCode = settings.ParkCode,
        ParkPickupPrefix = settings.ParkPickupPrefix,
        ConnectWisePlatformBaseUrl = settings.ConnectWisePlatformBaseUrl,
        ConnectWisePlatformClientId = settings.ConnectWisePlatformClientId,
        ConnectWisePlatformClientSecret = settings.ConnectWisePlatformClientSecret,
        ConnectWisePlatformClientSecretProtected = settings.ConnectWisePlatformClientSecretProtected,
        ConnectWisePlatformScopes = settings.ConnectWisePlatformScopes,
        ConnectWisePlatformAccessToken = settings.ConnectWisePlatformAccessToken,
        ConnectWisePlatformAccessTokenProtected = settings.ConnectWisePlatformAccessTokenProtected,
        ConnectWisePlatformAccessTokenExpiresAt = settings.ConnectWisePlatformAccessTokenExpiresAt,
        RegistrationEnabled = settings.RegistrationEnabled
    };

    private static void SetLaunchAtStartup(bool enabled)
    {
        WindowsStartupRegistration.Set(enabled);
    }

    private void RepairLaunchAtStartupRegistration()
    {
        if (!_settings.LaunchAtStartup) return;
        try
        {
            if (!WindowsStartupRegistration.IsCurrentExecutableRegistered())
                WindowsStartupRegistration.Set(enabled: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException or InvalidOperationException or ArgumentException)
        {
            App.LogStartup($"Windows startup registration warning: {ex.GetType().Name}");
        }
    }

    private async Task ActivateSelectedRowAsync(ListBox list)
    {
        if (list.SelectedItem is not RowItem row) return;

        if (row.Action is SetUpAction) { ShowSettings(); ShowSettingsSection("General"); return; }
        if (row.Action.Equals("Call", StringComparison.OrdinalIgnoreCase) || row.Action is CallVoicemailAction or PickUpAction)
        {
            var destination = row.Destination;
            if (string.IsNullOrWhiteSpace(destination)) destination = ExtractDestination(row.Detail);
            if (string.IsNullOrWhiteSpace(destination)) destination = row.Title;
            _suppressDialSearch = true;
            try { DialText.Text = destination; }
            finally { _suppressDialSearch = false; }
            await ToggleCallAsync();
            return;
        }

        if (row.Action.Equals("Configure", StringComparison.OrdinalIgnoreCase)) { ShowSettings(); return; }
        if (row.Action.Equals("Sync", StringComparison.OrdinalIgnoreCase)) { await SyncConnectWiseAsync(); return; }
        if (row.Action.Equals("Open", StringComparison.OrdinalIgnoreCase) && row.Title == "No live data") return;

        ShowInfo($"{row.Title}\n\n{row.Detail}\n\nThis provider-dependent workflow is not available with the current configuration.");
    }

    private async Task ActivateListRowOnEnterAsync(ListBox list, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ActivateSelectedRowAsync(list);
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
        ApplyControlMetadata();
        SetActiveNav(HomeTabButton);
        UpdatePhoneControlState();
    }

    private void ApplyControlMetadata()
    {
        SetControlMetadata(HomeTabButton, "Home", "Recent calls, contact search, and dialing.");
        SetControlMetadata(MoreTabButton, "More", "Voicemail, parking, recordings, and messages.");
        SetControlMetadata(SettingsButton, "Settings", "Audio, sign-in, SIP, ConnectWise, and app settings.");
        SetControlMetadata(KeypadToggleButton, "Show keypad", "Show or hide the dial keypad.");
        SetControlMetadata(StatusBannerDismissButton, "Dismiss status", "Hide this status message.");
        SetControlMetadata(RegisterButton, "Register provider", "Register or unregister the selected phone provider.");
        SetControlMetadata(DialText, "Search or dial", "Search contacts or enter a phone number or extension. Press Enter to call and Escape to clear.");
        SetControlMetadata(CallButton, "Start or end call", "Start a call to the entered destination or end the active call.");
        SetControlMetadata(MuteButton, "Mute call", "Mute or unmute microphone audio during a call.");
        SetControlMetadata(HoldButton, "Hold call", "Place the active call on hold or resume it.");
        SetControlMetadata(TransferButton, "Transfer call", "Transfer the active call to another destination.");
        SetControlMetadata(DtmfButton, "Send DTMF", "Send touch-tone digits during an active call.");
        SetControlMetadata(SyncConnectWiseButton, "Sync ConnectWise", "Synchronize ConnectWise contacts into the CallBridge directory.");
        SetControlMetadata(CreateTicketButton, "New ConnectWise ticket", "Create a ConnectWise ticket. Select a row first to use its company, or choose the company in the dialog.");
        SetControlMetadata(VoicemailCardCallButton, "Call voicemail", "Dial your voicemail.");
        SetControlMetadata(ExportHistoryButton, "Export call history", "Export call history to CSV.");
        SetControlMetadata(AddContactButton, "Add contact", "Add a local CallBridge contact.");
        SetControlMetadata(ImportContactsButton, "Import contacts", "Import contacts from a CSV file.");
        SetControlMetadata(ClearContactsButton, "Clear imported contacts", "Remove imported contacts from the local directory.");
        SetControlMetadata(RefreshRowsButton, "Refresh list", "Reload the current list.");

        foreach (Button button in DialPad.Children.OfType<Button>())
        {
            var label = button.Content?.ToString() ?? "";
            var digit = label.Length > 0 ? label[0].ToString() : label;
            SetControlMetadata(button, $"Dial {digit}", $"Enter digit {digit}.");
        }
    }

    private static void SetControlMetadata(FrameworkElement element, string name, string tooltip)
    {
        element.ToolTip = tooltip;
        ToolTipService.SetShowOnDisabled(element, true);
        AutomationProperties.SetName(element, name);
    }

    private async Task HandleDialTextKeyDownAsync(KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ToggleCallAsync();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_activeCallId is null && _sipCallId is null) DialText.Clear();
        }
    }

    private void ToggleKeypad()
    {
        _keypadOpen = !_keypadOpen;
        var show = _keypadOpen;
        ApplyHomeLayout();
        KeypadToggleButton.Background = show ? (Brush)FindResource("AccentSoft") : Brushes.Transparent;
        AutomationProperties.SetName(KeypadToggleButton, show ? "Hide keypad" : "Show keypad");
    }

    private void SetActiveNav(Button activeButton)
    {
        foreach (var button in NavButtons())
            button.Tag = ReferenceEquals(button, activeButton) ? ActiveTabTag : null;
        SettingsButton.Foreground = ReferenceEquals(activeButton, SettingsButton) ? Brush(AccentHex) : Brush(MutedHex);
    }

    private IEnumerable<Button> NavButtons()
    {
        yield return HomeTabButton;
        yield return MoreTabButton;
        yield return SettingsButton;
    }

    private void LoadSettings()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var legacyPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        var loaded = AppSettingsStore.Load(_settingsPath, legacyPath);
        _settings = loaded.Settings;
        if (loaded.RecoveredCorruptFile) App.LogStartup("Recovered from an unreadable settings file.");
        _settings.ApiBase = LocalApi.DefaultBase;
        if (_settings.Provider is "Mock provider" or "Standard SIP test") _settings.Provider = StandardSipProvider;
        if (_settings.ConnectWisePlatformAccessTokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            _settings.ConnectWisePlatformAccessToken = "";
            _settings.ConnectWisePlatformAccessTokenProtected = "";
            _settings.ConnectWisePlatformAccessTokenExpiresAt = null;
        }
        if (_settings.Microphone == WindowsAudioDeviceCatalog.LegacyDefaultDeviceName) _settings.Microphone = WindowsAudioDeviceCatalog.DefaultDeviceId;
        if (_settings.Speaker == WindowsAudioDeviceCatalog.LegacyDefaultDeviceName) _settings.Speaker = WindowsAudioDeviceCatalog.DefaultDeviceId;
        if (_settings.RingtoneSpeaker == WindowsAudioDeviceCatalog.LegacyDefaultDeviceName) _settings.RingtoneSpeaker = WindowsAudioDeviceCatalog.DefaultDeviceId;
        _sip.ConfigureAudioDevices(_settings.Microphone, _settings.Speaker);
        _sip.ConfigureCodecProfile(_settings.SipCodecProfileName);
        if (loaded.CanSave) SaveSettings(false);
    }

    private bool SaveSettings(bool apply)
    {
        var saved = true;
        try
        {
            AppSettingsStore.Save(_settingsPath, _settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            saved = false;
            if (apply && !_settingsSaveWarningShown)
            {
                _settingsSaveWarningShown = true;
                ShowWarning($"CallBridge is running, but settings could not be saved.\n\n{ex.Message}");
            }
        }

        if (apply) ApplyBranding();
        return saved;
    }

    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private void ApplyBranding()
    {
        Title = $"{_settings.ProductName} - {_settings.CompanyName}";
        BrandTitle.Text = _settings.ProductName;
        Branding.Apply(_settings.ProductName, _settings.BrandLogoPath, _settings.BrandAccentColor);
        ApplyPhoneFeatureSettings();
        ExtensionText.Text = string.IsNullOrWhiteSpace(_settings.Extension) ? "" : $"Ext {_settings.Extension}";
        UpdateProviderStatus();
        VersionText.Text = $"CallBridge v{Version}";
    }

    private void ApplyResponsiveLayout()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var narrow = width < 700;
        var tiny = width < 600;
        BrandTitle.Visibility = tiny || Branding.Logo is not null ? Visibility.Collapsed : Visibility.Visible;
        ExtensionText.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ContentHost.Margin = narrow ? new Thickness(10) : new Thickness(14);
        foreach (var grid in new[] { SettingsGeneralGrid, SettingsAudioGrid, SettingsSipGrid, SettingsCwPlatformGrid, SettingsCwPsaGrid, SettingsBrandingGrid })
            grid.Columns = narrow ? 1 : 2;
        InCallControls.Columns = tiny ? 3 : 5;
        InCallControls.Rows = tiny ? 2 : 1;
        ApplyHomeLayout();
    }

    /// <summary>Home shows recent calls; typing in the search/dial box switches the list to matching contacts.</summary>
    private async Task ShowHomeAsync()
    {
        // Always leave More or Settings. SearchFromDialTextAsync ignores typing while those views are open,
        // so Home navigates directly instead of routing through it.
        var searching = !string.IsNullOrWhiteSpace(DialText.Text) && _activeCallId is null && _sipCallId is null;
        if (searching) await ShowContactSearchAsync();
        else await NavigateToRowsAsync(RecentCallsTitle, "Your latest inbound, outbound, and missed calls", HistoryRowsAsync);
    }

    private Task ShowContactSearchAsync() =>
        NavigateToRowsAsync(ContactsListTitle, "Matching contacts from the directory", async () =>
        {
            if (_contactRowsCache is not null) return _contactRowsCache;
            var rows = await ContactRowsAsync();
            // Placeholder rows mean the directory failed or is empty; don't cache them so the next search retries.
            if (rows.Any(row => row.Action == "Call")) _contactRowsCache = rows;
            return rows;
        });

    private async Task SearchFromDialTextAsync()
    {
        if (_suppressDialSearch || _listTitle == MoreListTitle || SettingsView.Visibility == Visibility.Visible) return;
        if (_activeCallId is not null || _sipCallId is not null) return;
        var query = DialText.Text.Trim();
        if (query.Length == 0)
        {
            if (_listTitle == ContactsListTitle) await NavigateToRowsAsync(RecentCallsTitle, "Your latest inbound, outbound, and missed calls", HistoryRowsAsync);
            return;
        }

        if (_listTitle != ContactsListTitle || _contactRowsCache is null)
        {
            await ShowContactSearchAsync();
            return;
        }
        RenderRows(FilterRows(query));
    }

    private void ShowRows(string title, string subtitle, List<RowItem> rows)
    {
        _viewRequestVersion++;
        ShowRowsCore(title, subtitle, rows, false);
    }

    private async Task NavigateToRowsAsync(string title, string subtitle, Func<Task<List<RowItem>>> loader)
    {
        var requestVersion = ++_viewRequestVersion;
        ShowRowsCore(title, subtitle, [new("Loading...", "Fetching the latest CallBridge data", "Please wait")], true);
        var rows = await loader();
        if (requestVersion != _viewRequestVersion) return;
        _currentRows = rows;
        RowsList.IsEnabled = true;
        RenderRows(title == ContactsListTitle ? FilterRows(DialText.Text.Trim()) : rows);
    }

    private void ShowRowsCore(string title, string subtitle, List<RowItem> rows, bool loading)
    {
        _listTitle = title;
        _adminUnlocked = false;
        var more = title == MoreListTitle;
        var contacts = title == ContactsListTitle;
        var recent = title == RecentCallsTitle;
        SetActiveNav(more ? MoreTabButton : HomeTabButton);
        ListHeading.Text = title;
        ListSubheading.Text = subtitle;
        _currentRows = rows;
        SettingsView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        CreateTicketButton.Visibility = contacts || recent ? Visibility.Visible : Visibility.Collapsed;
        ExportHistoryButton.Visibility = recent ? Visibility.Visible : Visibility.Collapsed;
        RefreshRowsButton.Visibility = more ? Visibility.Collapsed : Visibility.Visible;
        ApplyHomeLayout();
        RowsList.IsEnabled = !loading;
        RenderRows(rows);
    }

    private void UpdateListActionState()
    {
        // Per-row actions live on each row; only the toolbar New ticket reflects the selection.
        var selected = RowsList.SelectedItem as RowItem;
        var hasCompany = selected is not null && ConnectWiseClient.IsCompanyId(selected.CompanyId);
        CreateTicketButton.IsEnabled = true;
        CreateTicketButton.ToolTip = hasCompany
            ? $"Create a ConnectWise ticket for {selected!.Company}."
            : "Create a ConnectWise ticket. You can choose the company in the dialog.";
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
            ShowWarning("Select a contact first."); return;
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
            await DirectoryChangedAsync();
        }
        catch (Exception ex) { ShowWarning($"Could not save contact: {ex.Message}"); }
    }

    private async Task DeleteSelectedContactAsync()
    {
        if (RowsList.SelectedItem is not RowItem row || string.IsNullOrWhiteSpace(row.ContactId))
        {
            ShowWarning("Select a contact first."); return;
        }
        if (!ConfirmWarning($"Delete {row.Title}?")) return;
        try
        {
            using var response = await _http.DeleteAsync($"{_settings.ApiBase.TrimEnd('/')}/directory/contacts/{Uri.EscapeDataString(row.ContactId)}");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            await DirectoryChangedAsync();
        }
        catch (Exception ex) { ShowWarning($"Could not delete contact: {ex.Message}"); }
    }

    private async Task RefreshCurrentViewAsync()
    {
        if (_listTitle == MoreListTitle) ShowRows(MoreListTitle, MoreSubtitle, MoreRows());
        else if (_listTitle == ContactsListTitle)
        {
            _contactRowsCache = null;
            await SearchFromDialTextAsync();
        }
        else await NavigateToRowsAsync(RecentCallsTitle, "Your latest inbound, outbound, and missed calls", HistoryRowsAsync);
    }

    /// <summary>Drops cached directory rows after the directory changes and refreshes a visible contact search.</summary>
    private async Task DirectoryChangedAsync()
    {
        _contactRowsCache = null;
        if (_listTitle == ContactsListTitle && HomeView.Visibility == Visibility.Visible) await SearchFromDialTextAsync();
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
            await DirectoryChangedAsync();
            ShowInlineStatus($"Imported {records.Count} contacts. They are saved in CallBridge and ready to search or dial.");
        }
        catch (Exception ex) { ShowWarning($"Import failed: {ex.Message}"); }
    }

    private async Task SyncConnectWiseAsync()
    {
        if (!ConnectWiseTicketing.IsConfigured(_settings))
        {
            ShowWarning("Connect ConnectWise in Settings first.");
            ShowSettings(); return;
        }
        try
        {
            SetBusyAction(SyncConnectWiseButton, "Syncing ConnectWise contacts...");
            var progress = new Progress<string>(message => ShowInlineStatus(message));
            using var client = new ConnectWiseTicketing(_settings);
            var contacts = await client.DownloadDirectoryAsync(progress);
            var records = contacts.Select(contact => new { companyId = contact.CompanyId, companyName = contact.CompanyName, contactId = contact.ContactId, contactName = contact.ContactName, phones = contact.Phones }).ToList();
            var json = JsonSerializer.Serialize(new { records }, JsonOptions());
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{_settings.ApiBase.TrimEnd('/')}/integrations/connectwise/directory") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            await DirectoryChangedAsync();
            ShowInlineStatus($"Synchronized {contacts.Count} ConnectWise contacts with callable phone records.");
        }
        catch (Exception ex) { ShowWarning($"ConnectWise sync failed: {ex.Message}"); }
        finally { RestoreAction(SyncConnectWiseButton, "Synchronize ConnectWise contacts into the CallBridge directory."); }
    }

    private static void SetBusyAction(Button button, string tooltip)
    {
        button.IsEnabled = false;
        button.ToolTip = tooltip;
    }

    private static void RestoreAction(Button button, string tooltip)
    {
        button.IsEnabled = true;
        button.ToolTip = tooltip;
    }

    private void OpenSelectedCompany()
    {
        if (RowsList.SelectedItem is not RowItem row || string.IsNullOrWhiteSpace(row.CompanyId)) { ShowWarning("Select a synchronized ConnectWise contact first."); return; }
        if (!ConnectWiseTicketing.IsConfigured(_settings)) { ShowWarning("Configure ConnectWise in Settings first."); return; }
        try
        {
            using var client = new ConnectWiseTicketing(_settings);
            if (client.CompanyUrl(row.CompanyId) is not { } url) { ShowWarning($"Open {row.Company} in ConnectWise. The platform API doesn't provide a direct link."); return; }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowWarning($"Could not open company: {ex.Message}"); }
    }

    private async Task CreateTicketForSelectedContactAsync()
    {
        if (ConnectWiseTicketing.TicketCreationProblem(_settings) is { } problem) { ShowWarning(problem); return; }
        var row = RowsList.SelectedItem as RowItem;
        var isPlaceholder = row is null || row.Title is "No live data" or "Loading...";
        var company = isPlaceholder ? "" : row!.Company;
        var contact = isPlaceholder ? "" : row!.Title;
        var phone = isPlaceholder ? "" : row!.Destination;
        var editor = new TicketWindow(company, contact, phone, isPlaceholder ? "" : row!.CompanyId, SearchCompaniesAsync) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try
        {
            using var client = new ConnectWiseTicketing(_settings);
            var ticket = await client.CreateTicketAsync(editor.CompanyId, editor.CompanyName, editor.Summary, editor.Description);
            ShowInlineStatus($"ConnectWise ticket #{ticket.DisplayNumber} was created for {(string.IsNullOrWhiteSpace(editor.CompanyName) ? "the selected company" : editor.CompanyName)}.");
            if (client.TicketUrl(ticket.Id) is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowWarning($"Ticket creation failed: {ex.Message}"); }
    }

    private async Task ClearImportedContactsAsync()
    {
        if (!ConfirmWarning("Remove all contacts previously loaded by CSV import?")) return;
        try
        {
            using var response = await _http.DeleteAsync($"{_settings.ApiBase.TrimEnd('/')}/directory");
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            await DirectoryChangedAsync();
        }
        catch (Exception ex) { ShowWarning($"Could not clear imported contacts: {ex.Message}"); }
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
        RowsList.SelectedItem = null;
        UpdateListActionState();
    }

    private IEnumerable<RowItem> FilterRows(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _currentRows;
        var digits = new string(query.Where(char.IsDigit).ToArray());
        var phoneQuery = digits.Length >= 3 && query.All(ch => char.IsDigit(ch) || ch is ' ' or '+' or '-' or '(' or ')' or '.');
        var matches = _currentRows
            .Where(r => $"{r.Title} {r.Detail} {r.Action}".Contains(query, StringComparison.OrdinalIgnoreCase)
                || (phoneQuery && new string($"{r.Destination} {r.Detail}".Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)))
            .ToList();
        return matches.Count > 0 ? matches : EmptyRows($"No results match \"{query}\". Press Escape to clear search.");
    }

    private async Task RefreshHealthAsync(bool forceRecovery = false)
    {
        await _serviceHealthGate.WaitAsync();
        try
        {
            if (await IsLocalServiceReadyAsync())
            {
                SetLocalServiceReady();
                return;
            }

            _consecutiveServiceHealthFailures++;
            var processExited = _localServiceProcess is null || _localServiceProcess.HasExited;
            if (_ownsLocalService && !_shutdownStarted
                && (forceRecovery || processExited || _consecutiveServiceHealthFailures >= 3))
            {
                await RestartOwnedLocalServiceAsync();
                if (await WaitForLocalServiceAsync(TimeSpan.FromSeconds(4)))
                {
                    SetLocalServiceReady();
                    return;
                }
            }

            SetLocalServiceUnavailable();
        }
        catch
        {
            SetLocalServiceUnavailable();
        }
        finally
        {
            _serviceHealthGate.Release();
        }
    }

    private async Task<bool> IsLocalServiceReadyAsync()
    {
        try
        {
            using var response = await _http.GetAsync($"{_settings.ApiBase.TrimEnd('/')}/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> WaitForLocalServiceAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!_shutdownStarted && DateTimeOffset.UtcNow < deadline)
        {
            if (await IsLocalServiceReadyAsync()) return true;
            await Task.Delay(150);
        }
        return false;
    }

    private async Task RestartOwnedLocalServiceAsync()
    {
        if (!_ownsLocalService || _shutdownStarted) return;
        App.LogStartup("Restarting the desktop-owned local service.");
        await StopOwnedLocalServiceAsync();
        if (_shutdownStarted) return;
        StartOwnedLocalService();
    }

    private void SetLocalServiceReady()
    {
        _consecutiveServiceHealthFailures = 0;
        _serviceReady = true;
        if (_serviceBannerShown) { StatusBanner.Visibility = Visibility.Collapsed; _serviceBannerShown = false; }
    }

    private void SetLocalServiceUnavailable()
    {
        _serviceReady = false;
        ShowBanner("CallBridge app services are unavailable. Some features won't work until they recover.", WarnHex);
        _serviceBannerShown = true;
    }

    internal async Task RunLocalServiceRecoverySmokeAsync()
    {
        if (!_ownsLocalService || _localServiceProcess is null)
            throw new InvalidOperationException("The recovery smoke test requires a desktop-owned local service.");
        if (!await WaitForLocalServiceAsync(TimeSpan.FromSeconds(5)))
            throw new InvalidOperationException("The local service was not ready before the recovery test.");

        var originalProcessId = _localServiceProcess.Id;
        _localServiceProcess.Kill(true);
        await _localServiceProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await RefreshHealthAsync(forceRecovery: true);
        if (_localServiceProcess is null
            || _localServiceProcess.HasExited
            || _localServiceProcess.Id == originalProcessId
            || !await IsLocalServiceReadyAsync())
            throw new InvalidOperationException("CallBridge did not recover its local service.");
    }


    private static bool IsSipBackedProvider(string provider) =>
        provider is StandardSipProvider or AxionHivePbxProvider;

    private void ApplyProviderPreset()
    {
        // Presets contain only public interoperability defaults, never tenant credentials.
        if (_settings.Provider == AxionHivePbxProvider)
        {
            _settings.SipTransport = "UDP";
            _settings.SipCodecProfileName = SipCodecProfile.AxionName;
            if (string.IsNullOrWhiteSpace(_settings.VoicemailAccessCode)) _settings.VoicemailAccessCode = HivePbxVoicemailCode;
            if (string.IsNullOrWhiteSpace(_settings.ParkCode)) _settings.ParkCode = HivePbxParkCode;
            if (string.IsNullOrWhiteSpace(_settings.ParkSlots)) _settings.ParkSlots = HivePbxParkSlots;
        }
    }
    private async Task ToggleRegistrationAsync()
    {
        ApplyProviderPreset();
        if (_settings.Provider == AxionNoixaPendingProvider)
        {
            ShowWarning("Axion/Noixa calling requires their approved SBC/WebRTC integration details. This provider is intentionally blocked until those are supplied.");
            return;
        }

        if (IsSipBackedProvider(_settings.Provider))
        {
            if (_sip.IsRegistered || _registrationRequested)
            {
                _settings.RegistrationEnabled = false;
                SaveSettings(false);
                _registrationRequested = false;
                CancelSipRecovery();
                await _sip.UnregisterAsync();
                _registered = false;
                UpdateProviderStatus();
                return;
            }
            if (!HasSipRegistrationSettings())
            {
                ShowWarning("Add the SIP server, SBC extension username, and password in Settings first.");
                return;
            }
            _settings.RegistrationEnabled = true;
            SaveSettings(false);
            await StartConfiguredRegistrationAsync(showFailure: true);
            return;
        }
        ShowWarning("Select a SIP-backed provider and add registrar credentials before registering.");
    }

    private async Task StartConfiguredRegistrationAsync(bool showFailure)
    {
        if (!SipRegistrationStartupPolicy.ShouldStart(
                _settings.RegistrationEnabled,
                _registrationRequested,
                _sip.IsRegistered,
                IsSipBackedProvider(_settings.Provider),
                HasSipRegistrationSettings(),
                _shutdownStarted)) return;

        _registrationRequested = true;
        UpdateProviderStatus();
        _sip.ConfigureAudioDevices(_settings.Microphone, _settings.Speaker);
        _sip.ConfigureCodecProfile(_settings.SipCodecProfileName);
        var sipResult = await _sip.RegisterAsync(_settings.SipServer, _settings.SipUsername, _settings.SipPassword, _settings.SipTransport);
        if (_shutdownStarted) return;
        _registered = sipResult.Success;
        if (!sipResult.Success && !_sip.IsRegistrationRunning) _registrationRequested = false;
        UpdateProviderStatus();
        if (!sipResult.Success)
        {
            App.LogStartup("Automatic SIP registration did not complete.");
            if (showFailure) ShowApiError(sipResult.Message);
        }
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
        UpdateRegistrationControlState();
        PresenceText.Text = _registered ? "Available" : _registrationRequested ? "Connecting" : "Offline";
        PresenceDot.Fill = _registered ? Brush(GoodHex) : _registrationRequested ? Brush(WarnHex) : Brush(MutedHex);
        CallStateText.Text = _registered ? $"READY - {_settings.Provider.ToUpperInvariant()}" : "READY";
        UpdatePhoneControlState();
        UpdateTrayStatus();
        UpdateSignInStatus();
        UpdateStatusCards();
    }

    private void UpdateRegistrationControlState()
    {
        if (_settings.Provider == AxionNoixaPendingProvider)
        {
            RegisterButton.Content = "Unavailable";
            RegisterButton.IsEnabled = false;
            RegisterButton.ToolTip = "Axion/Noixa calling requires approved SBC/WebRTC integration details before registration is available.";
            return;
        }

        if (!IsSipBackedProvider(_settings.Provider))
        {
            RegisterButton.Content = "Register";
            RegisterButton.IsEnabled = false;
            RegisterButton.ToolTip = "Select a SIP-backed provider before registering.";
            return;
        }

        if (_registered)
        {
            RegisterButton.Content = "Unregister";
            RegisterButton.IsEnabled = true;
            RegisterButton.ToolTip = "Unregister the selected phone provider.";
            return;
        }

        if (_registrationRequested)
        {
            RegisterButton.Content = "Stop";
            RegisterButton.IsEnabled = true;
            RegisterButton.ToolTip = "Stop trying to register the selected phone provider.";
            return;
        }

        RegisterButton.Content = "Register";
        RegisterButton.IsEnabled = HasSipRegistrationSettings();
        RegisterButton.ToolTip = RegisterButton.IsEnabled
            ? "Register the selected phone provider."
            : "Add SIP server, username, and password in Settings before registering.";
    }

    private bool HasSipRegistrationSettings() =>
        !string.IsNullOrWhiteSpace(_settings.SipServer)
        && !string.IsNullOrWhiteSpace(_settings.SipUsername)
        && !string.IsNullOrWhiteSpace(_settings.SipPassword)
        && SipTransportProfile.TryParse(_settings.SipTransport, out _);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
        ThemeWindows.TintTitleBar(this);
        if (_windowSource is not null)
        {
            _incomingCallNotifier = new WindowsIncomingCallNotifier(_windowSource.Handle);
            UpdateTrayStatus();
        }
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmPowerBroadcast && (wParam.ToInt32() == PowerResumeAutomatic || wParam.ToInt32() == PowerResumeSuspend))
            ScheduleSipRecovery("Windows resumed");
        else if (message == WindowsIncomingCallNotifier.TaskbarCreatedMessage && message != 0)
            _incomingCallNotifier?.Restore();
        else if (message == WindowsIncomingCallNotifier.CallbackMessage)
        {
            switch (WindowsIncomingCallNotifier.ClassifyMessage(lParam))
            {
                case TrayIconAction.OpenFlyout:
                    handled = true;
                    ShowTrayFlyout();
                    break;
                case TrayIconAction.ActivateWindow:
                    handled = true;
                    OpenFromTray(showSettings: false);
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => ScheduleSipRecovery("Network changed");

    private void ScheduleSipRecovery(string reason)
    {
        if (_shutdownStarted || !_registrationRequested) return;
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _sipRecoveryCts, replacement);
        previous?.Cancel();
        _ = RecoverSipRegistrationAsync(reason, replacement);
    }

    private async Task RecoverSipRegistrationAsync(string reason, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), source.Token);
            await _sipRecoveryGate.WaitAsync(source.Token);
            try
            {
                var request = await Dispatcher.InvokeAsync(() => new
                {
                    CanReconnect = SipRegistrationRecoveryPolicy.CanReconnect(
                        _registrationRequested,
                        HasSipRegistrationSettings() && IsSipBackedProvider(_settings.Provider),
                        _shutdownStarted,
                        _sip.CurrentCall.State),
                    Server = _settings.SipServer,
                    Username = _settings.SipUsername,
                    Password = _settings.SipPassword,
                    Transport = _settings.SipTransport
                });
                if (!request.CanReconnect)
                {
                    if (_registrationRequested && _sip.CurrentCall.State is not (SipCallState.Idle or SipCallState.Failed))
                        _sipRecoveryPending = true;
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    PresenceText.Text = "Connecting";
                    UpdateRegistrationControlState();
                });
                App.LogStartup($"SIP recovery started: {reason}.");
                var result = await _sip.RegisterAsync(request.Server, request.Username, request.Password, request.Transport);
                if (source.IsCancellationRequested || !_registrationRequested)
                {
                    await _sip.UnregisterAsync();
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    _registered = result.Success;
                    if (!result.Success && !_sip.IsRegistrationRunning) _registrationRequested = false;
                    UpdateProviderStatus();
                });
            }
            finally
            {
                _sipRecoveryGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.LogStartup($"SIP recovery warning: {ex.GetType().Name}.");
            if (!_shutdownStarted) await Dispatcher.InvokeAsync(UpdateProviderStatus);
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref _sipRecoveryCts, null, source), source)) source.Dispose();
            else source.Dispose();
        }
    }

    private void CancelSipRecovery()
    {
        _sipRecoveryPending = false;
        var source = Interlocked.Exchange(ref _sipRecoveryCts, null);
        source?.Cancel();
    }

    private void UpdatePhoneControlState()
    {
        var sipStatus = _sip.CurrentCall;
        var waitingStatus = _sip.CurrentWaitingCall;
        var hasPendingIncomingCall = sipStatus.HasPendingIncomingCall || waitingStatus.HasPendingCall;
        var canAnswerIncomingCall = sipStatus.HasPendingIncomingCall || waitingStatus.CanAnswer;
        var canDeclineIncomingCall = sipStatus.HasPendingIncomingCall || waitingStatus.CanDecline;
        var hasEstablishedCall = sipStatus.HasEstablishedCall;
        var hasSipCallInProgress = sipStatus.State is not (SipCallState.Idle or SipCallState.Failed);
        var transferStatus = _sip.CurrentTransfer;
        var hasActiveCall = _activeCallId is not null || _sipCallId is not null || hasSipCallInProgress;
        var hasDestination = !string.IsNullOrWhiteSpace(DialText.Text);
        CallButton.IsEnabled = !hasPendingIncomingCall && (hasActiveCall || (_registered && hasDestination));
        CallButton.ToolTip = hasPendingIncomingCall
            ? "Answer or decline the incoming call first."
            : hasActiveCall
            ? "End the active call."
            : !_registered
                ? "Register the selected provider before starting a call."
                : hasDestination
                    ? "Start a call to the entered destination."
                    : "Enter a phone number or extension before starting a call.";
        InCallControls.Visibility = hasActiveCall && !hasPendingIncomingCall ? Visibility.Visible : Visibility.Collapsed;
        CallStatusRow.Visibility = hasActiveCall || hasPendingIncomingCall ? Visibility.Visible : Visibility.Collapsed;
        DialHost.Visibility = CallStatusRow.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ApplyHomeLayout();
        AnswerCallButton.IsEnabled = canAnswerIncomingCall;
        DeclineCallButton.IsEnabled = canDeclineIncomingCall;
        MuteButton.IsEnabled = hasEstablishedCall && (!transferStatus.IsActive || transferStatus.State == SipTransferState.ConsultationConnected);
        HoldButton.IsEnabled = hasEstablishedCall && !transferStatus.IsActive;
        TransferButton.IsEnabled = hasEstablishedCall && !waitingStatus.HasTwoCalls && (!transferStatus.IsActive || transferStatus.CanComplete);
        ParkButton.IsEnabled = hasEstablishedCall && !waitingStatus.HasTwoCalls && !transferStatus.IsActive;
        if (!ParkButton.IsEnabled) ParkPanel.Visibility = Visibility.Collapsed;
        DtmfButton.IsEnabled = transferStatus.CanCancel || (hasEstablishedCall && !transferStatus.IsActive);
        HoldButton.Content = waitingStatus.HasTwoCalls ? "Switch" : sipStatus.State == SipCallState.Held ? "Resume" : "Hold";
        HoldButton.ToolTip = waitingStatus.HasTwoCalls
            ? $"Switch to the held call with {waitingStatus.HeldParty}."
            : sipStatus.State == SipCallState.Held ? "Resume the active call." : "Place the active call on hold.";
        AutomationProperties.SetName(HoldButton, waitingStatus.HasTwoCalls ? "Switch active call" : HoldButton.Content.ToString());
    }

    private void ApplySipCallStatus(SipCallStatus status)
    {
        _sipCallDirection = status.Direction;
        _sipRemoteParty = status.RemoteParty;
        _sipRemoteNumber = status.RemoteNumber;
        CallStateText.Text = status.State switch
        {
            SipCallState.OutboundDialing => "CALLING",
            SipCallState.OutboundRinging => status.Detail == "Early media" ? "EARLY MEDIA" : "RINGING",
            SipCallState.IncomingRinging => "INCOMING CALL",
            SipCallState.Connecting => "CONNECTING",
            SipCallState.Connected => "CONNECTED",
            SipCallState.Held => "CALL ON HOLD",
            SipCallState.Ending => "ENDING CALL",
            SipCallState.Failed => "CALL FAILED",
            _ => "READY"
        };

        var waitingStatus = _sip.CurrentWaitingCall;
        var hasWaitingCall = waitingStatus.HasPendingCall;
        var hasIncomingCall = status.HasPendingIncomingCall || hasWaitingCall;
        IncomingCallPanel.Visibility = hasIncomingCall ? Visibility.Visible : Visibility.Collapsed;
        IncomingCallLabel.Text = hasWaitingCall ? "Call waiting" : "Incoming call";
        IncomingCallerText.Text = hasWaitingCall
            ? (string.IsNullOrWhiteSpace(waitingStatus.IncomingParty) ? "Unknown caller" : waitingStatus.IncomingParty)
            : (string.IsNullOrWhiteSpace(status.RemoteParty) ? "Unknown caller" : status.RemoteParty);
        if (status.State == SipCallState.IncomingRinging || waitingStatus.State == SipCallWaitingState.Ringing)
        {
            if (!_ringer.IsPlaying) _ringer.Start(_settings.RingtoneSpeaker);
        }
        else
        {
            _ringer.Stop();
            _incomingCallNotifier?.Clear();
            CloseIncomingCallWindow();
        }

        if (status.State == SipCallState.IncomingRinging)
        {
            if (_sipCallId is null)
            {
                _sipCallId = Guid.NewGuid().ToString("N");
                _activeCallId = _sipCallId;
                BeginCallContext(IncomingCallerText.Text, _sipRemoteNumber);
                ShowIncomingCallWindow(IncomingCallerText.Text, _sipRemoteNumber);
                _incomingCallNotifier?.Show(IncomingCallerText.Text);
                UpdateTrayStatus();
            }
            ActiveCallMetric.Text = IncomingCallerText.Text;
            CallButton.Content = "Call pending";
            CallButton.Background = Brush(MutedHex);
            Topmost = _settings.AlwaysOnTopDuringCalls;
        }
        else if (status.State == SipCallState.Connected)
        {
            if (_sipCallId is null)
            {
                _sipCallId = Guid.NewGuid().ToString("N");
                _activeCallId = _sipCallId;
                BeginCallContext(status.RemoteParty, status.RemoteNumber);
            }
            MarkCallConnected();
            if (_callContext?.ContactName is not { Length: > 0 })
                ActiveCallMetric.Text = string.IsNullOrWhiteSpace(status.RemoteParty) ? DialText.Text : status.RemoteParty;
            CallButton.Content = "Hang up";
            CallButton.Background = Brush(BadHex);
            Topmost = _settings.AlwaysOnTopDuringCalls;
        }
        else if (status.State == SipCallState.Ending && _activeCallId is not null)
        {
            EndLocalCall("READY");
        }
        else if (status.State == SipCallState.Failed && _activeCallId is not null)
        {
            EndLocalCall("CALL FAILED");
        }

        if (status.State is SipCallState.Idle or SipCallState.Failed && _sipRecoveryPending)
        {
            _sipRecoveryPending = false;
            ScheduleSipRecovery("Call ended");
        }

        UpdatePhoneControlState();
        UpdateTrayStatus();
    }

    private void ApplySipCallWaitingStatus(SipCallWaitingStatus status)
    {
        if (status.State == SipCallWaitingState.Ringing)
        {
            IncomingCallLabel.Text = "Call waiting";
            IncomingCallerText.Text = string.IsNullOrWhiteSpace(status.IncomingParty) ? "Unknown caller" : status.IncomingParty;
            IncomingCallPanel.Visibility = Visibility.Visible;
            CallStateText.Text = "CALL WAITING";
            if (!_ringer.IsPlaying) _ringer.Start(_settings.RingtoneSpeaker);
            _incomingCallNotifier?.Show($"Call waiting: {IncomingCallerText.Text}");
        }
        else if (status.State == SipCallWaitingState.Answering)
        {
            IncomingCallPanel.Visibility = Visibility.Collapsed;
            _ringer.Stop();
            _incomingCallNotifier?.Clear();
            CallStateText.Text = "ANSWERING WAITING CALL";
        }
        else if (status.State == SipCallWaitingState.TwoCalls)
        {
            IncomingCallPanel.Visibility = Visibility.Collapsed;
            _ringer.Stop();
            _incomingCallNotifier?.Clear();
            CallStateText.Text = "CONNECTED - 2 CALLS";
            ActiveCallMetric.Text = string.IsNullOrWhiteSpace(status.ActiveParty) ? "Active call" : status.ActiveParty;
            ActiveCallMetric.ToolTip = string.IsNullOrWhiteSpace(status.HeldParty) ? null : $"On hold: {status.HeldParty}";
            _held = false;
        }
        else if (!_sip.CurrentCall.HasPendingIncomingCall)
        {
            IncomingCallPanel.Visibility = Visibility.Collapsed;
            _ringer.Stop();
            _incomingCallNotifier?.Clear();
            ActiveCallMetric.ToolTip = null;
        }

        UpdatePhoneControlState();
    }

    private void ApplySipTransferStatus(SipTransferStatus status)
    {
        var consultationActive = status.IsActive;
        TransferButton.Content = status.CanComplete ? "Complete" : "Transfer";
        TransferButton.ToolTip = status.CanComplete
            ? "Complete the attended transfer."
            : consultationActive
                ? "Wait for the consultation call to connect."
                : "Transfer the active call to another destination.";
        AutomationProperties.SetName(TransferButton, status.CanComplete ? "Complete attended transfer" : "Transfer call");
        DtmfButton.Content = status.CanCancel ? "Cancel" : "DTMF";
        DtmfButton.ToolTip = status.CanCancel
            ? "Cancel the consultation and resume the original call."
            : "Send touch-tone digits during an active call.";
        AutomationProperties.SetName(DtmfButton, status.CanCancel ? "Cancel attended transfer" : "Send DTMF");
        _held = consultationActive;
        HoldButton.Content = _held ? "Resume" : "Hold";

        if (consultationActive)
        {
            CallStateText.Text = status.State switch
            {
                SipTransferState.ConsultationDialing => "CONSULTING",
                SipTransferState.ConsultationRinging => status.Detail == "Early media" ? "CONSULTATION MEDIA" : "CONSULTATION RINGING",
                SipTransferState.ConsultationConnected => "CONSULTATION CONNECTED",
                SipTransferState.Completing => "COMPLETING TRANSFER",
                _ => "CONSULTING"
            };
            ActiveCallMetric.Text = string.IsNullOrWhiteSpace(status.Destination) ? "Consultation" : $"Consulting {status.Destination}";
        }
        else if (status.State == SipTransferState.Failed)
        {
            CallStateText.Text = "TRANSFER NOT COMPLETED";
            ShowBanner(status.Detail, WarnHex);
        }

        UpdatePhoneControlState();
    }

    private void ApplySipCallQuality(SipCallQualitySnapshot snapshot)
    {
        var summary = snapshot.HasMediaData ? snapshot.ToUserSummary() : "";
        CallQualityText.Text = string.IsNullOrEmpty(summary) ? "Waiting for call-quality data" : summary;
        CallStateText.ToolTip = string.IsNullOrEmpty(summary) ? null : summary;
        AutomationProperties.SetHelpText(CallStateText, summary);
    }

    private async Task AnswerIncomingCallAsync()
    {
        if (!_sip.CurrentCall.HasPendingIncomingCall && !_sip.CurrentWaitingCall.CanAnswer) return;
        var result = await _sip.AnswerIncomingAsync();
        if (!result.Success)
        {
            ShowApiError(result.Message);
            return;
        }

    }

    private async Task DeclineIncomingCallAsync()
    {
        if (!_sip.CurrentCall.HasPendingIncomingCall && !_sip.CurrentWaitingCall.CanDecline) return;
        await _sip.RejectIncomingAsync();
    }

    private async Task ToggleCallAsync()
    {
        if (_activeCallId is not null)
        {
            if (IsSipBackedProvider(_settings.Provider))
            {
                var hadTwoCalls = _sip.CurrentWaitingCall.HasTwoCalls;
                await _sip.HangupAsync();
                if (hadTwoCalls && _sip.CurrentCall.HasEstablishedCall)
                {
                    _sipCallDirection = _sip.CurrentCall.Direction;
                    _sipRemoteParty = _sip.CurrentCall.RemoteParty;
                    _sipRemoteNumber = _sip.CurrentCall.RemoteNumber;
                    ActiveCallMetric.Text = _sip.CurrentCall.RemoteParty;
                    CallButton.Content = "Hang up";
                    CallStateText.Text = "CONNECTED";
                    UpdatePhoneControlState();
                    return;
                }
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
            ShowWarning("Register the selected provider before starting a call.");
            return;
        }

        if (IsSipBackedProvider(_settings.Provider))
        {
            _sipCallId = Guid.NewGuid().ToString("N");
            _activeCallId = _sipCallId;
            _sipCallDirection = SipCallDirection.Outbound;
            _sipRemoteParty = DialText.Text;
            _sipRemoteNumber = DialText.Text;
            ActiveCallMetric.Text = DialText.Text;
            BeginCallContext(DialText.Text, DialText.Text);
            CallButton.Content = "Hang up";
            CallButton.Background = Brush(BadHex);
            UpdatePhoneControlState();
            CallStateText.Text = "DIALING";
            Topmost = _settings.AlwaysOnTopDuringCalls;
            var result = await _sip.DialAsync(DialText.Text);
            if (!result.Success)
            {
                EndLocalCall("CALL FAILED");
                ShowApiError(result.Message);
                return;
            }
            CallStateText.Text = $"CONNECTED - {_settings.Provider.ToUpperInvariant()}";
            return;
        }

        ShowWarning("The selected provider cannot place calls until its approved integration is configured.");
    }

    private void EndLocalCall(string state)
    {
        PreserveCallNotesOnEnd();
        OfferTimeEntryDraft();
        ResetCallWorkspace();
        _activeCallId = null;
        _sipCallId = null;
        _sipCallDirection = SipCallDirection.None;
        _sipRemoteParty = "";
        _sipRemoteNumber = "";
        _muted = false;
        _held = false;
        Topmost = false;
        CallStateText.Text = state;
        ActiveCallMetric.Text = "None";
        CallButton.Content = "Call";
        CallButton.Background = Brush(GoodHex);
        IncomingCallPanel.Visibility = Visibility.Collapsed;
        UpdatePhoneControlState();
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
        if (IsSipBackedProvider(_settings.Provider) && _sip.CurrentWaitingCall.CanSwap)
        {
            var switched = await _sip.SwapCallsAsync();
            if (!switched.Success) { ShowApiError(switched.Message); return; }
            _sipCallDirection = _sip.CurrentCall.Direction;
            _sipRemoteParty = _sip.CurrentCall.RemoteParty;
            _sipRemoteNumber = _sip.CurrentCall.RemoteNumber;
            ActiveCallMetric.Text = _sip.CurrentCall.RemoteParty;
            CallStateText.Text = "CONNECTED - 2 CALLS";
            return;
        }
        var requested = !_held;
        if (IsSipBackedProvider(_settings.Provider))
        {
            try
            {
                await _sip.SetHoldAsync(requested); _held = requested;
                HoldButton.Content = _held ? "Resume" : "Hold"; CallStateText.Text = _held ? "CALL ON HOLD" : "CONNECTED";
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
        if (IsSipBackedProvider(_settings.Provider) && _sip.CurrentTransfer.CanComplete)
        {
            var completed = await _sip.CompleteAttendedTransferAsync();
            if (!completed.Success) { ShowApiError(completed.Message); return; }
            EndLocalCall("TRANSFER COMPLETED");
            return;
        }

        var show = TransferPanel.Visibility != Visibility.Visible;
        ParkPanel.Visibility = Visibility.Collapsed;
        TransferPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) TransferTargetText.Focus();
    }

    private async Task ExecuteTransferAsync(bool consultFirst)
    {
        var destination = TransferTargetText.Text.Trim();
        if (_activeCallId is null || destination.Length == 0)
        {
            TransferTargetText.Focus();
            return;
        }

        if (IsSipBackedProvider(_settings.Provider))
        {
            var sipResult = consultFirst
                ? await _sip.BeginAttendedTransferAsync(destination)
                : await _sip.TransferAsync(destination);
            if (!sipResult.Success) { ShowApiError(sipResult.Message); return; }
            TransferPanel.Visibility = Visibility.Collapsed;
            if (consultFirst) return;
            EndLocalCall($"TRANSFERRED TO {destination}");
            return;
        }
        var result = await PostTelephonyAsync($"/telephony/calls/{_activeCallId}/transfer", new { destination });
        if (!result.Success) { ShowApiError(result.Message); return; }
        TransferPanel.Visibility = Visibility.Collapsed;
        EndLocalCall($"TRANSFERRED TO {destination}");
    }

    private async Task CancelAttendedTransferAsync()
    {
        var result = await _sip.CancelAttendedTransferAsync();
        if (!result.Success) { ShowApiError(result.Message); return; }
        _held = false;
        HoldButton.Content = "Hold";
        CallStateText.Text = "CONNECTED";
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
                var message = doc.RootElement.TryGetProperty("message", out var error) ? error.GetString() : null;
                return new(false, null, message ?? "CallBridge could not complete the operation.");
            }
            var callId = doc.RootElement.TryGetProperty("call", out var call) && call.TryGetProperty("id", out var id) ? id.GetString() : null;
            return new(true, callId, "");
        }
        catch (Exception)
        {
            _serviceReady = false;
            return new(false, null, "A required CallBridge service is unavailable. Restart CallBridge and try again.");
        }
    }

    private void ShowApiError(string message) => ShowWarning(message);

    private void ShowInfo(string message) =>
        MessageBox.Show(this, message, "CallBridge", MessageBoxButton.OK, MessageBoxImage.Information);

    private void ShowWarning(string message) =>
        MessageBox.Show(this, message, "CallBridge", MessageBoxButton.OK, MessageBoxImage.Warning);

    private bool ConfirmWarning(string message) =>
        MessageBox.Show(this, message, "CallBridge", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void ShowInlineStatus(string message)
    {
        ShowBanner(message, GoodHex);
        DirectoryStatusText.Text = message;
    }

    private void QueueSipLifecycleEvent(SipCallLifecycleEvent callEvent)
    {
        var task = _callEventQueue.EnqueueAndDrainAsync(callEvent, SendSipEventAsync);
        lock (_callEventTaskSync) _callEventTasks.Add(task);
        _ = ObserveCallEventTaskAsync(task);
    }

    private async Task ObserveCallEventTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            App.LogStartup($"Call history persistence warning: {ex.GetType().Name}");
        }
        finally
        {
            lock (_callEventTaskSync) _callEventTasks.Remove(task);
        }
    }

    private Task AwaitPendingCallEventsAsync()
    {
        lock (_callEventTaskSync) return Task.WhenAll(_callEventTasks.ToArray());
    }

    private async Task DrainSipEventQueueAsync()
    {
        try
        {
            await _callEventQueue.DrainAsync(SendSipEventAsync);
        }
        catch (Exception ex)
        {
            App.LogStartup($"Call history retry warning: {ex.GetType().Name}");
        }
    }

    private async Task<bool> SendSipEventAsync(SipCallLifecycleEvent callEvent)
    {
        try
        {
            var direction = callEvent.Direction == SipCallDirection.Inbound ? "inbound" : "outbound";
            var callerNumber = direction == "inbound" ? callEvent.RemoteNumber : _settings.CallerId;
            var calledNumber = direction == "inbound" ? _settings.Extension : callEvent.RemoteNumber;
            var payload = new
            {
                eventId = callEvent.EventId,
                callId = callEvent.CallId,
                direction,
                state = callEvent.ApiState,
                callerNumber,
                calledNumber,
                extension = _settings.Extension,
                occurredAt = callEvent.OccurredAt.UtcDateTime.ToString("O")
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions());
            using var response = await _http.PostAsync($"{_settings.ApiBase.TrimEnd('/')}/calls/events", new StringContent(json, Encoding.UTF8, "application/json"));
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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
                    var detail = $"{Capitalize(direction)} · {Value(call, "state")} · {duration} · {FormatCallTime(Value(call, "startedAt"))}";
                    if (!string.IsNullOrWhiteSpace(company)) detail += $" · {company}";
                    if (!string.IsNullOrWhiteSpace(outcome)) detail += $" · {outcome}";
                    var contactName = Value(call, "contactName");
                    var title = !string.IsNullOrWhiteSpace(contactName) ? contactName : string.IsNullOrWhiteSpace(number) ? "Unknown number" : number;
                    if (!string.IsNullOrWhiteSpace(contactName) && !string.IsNullOrWhiteSpace(number)) detail = $"{number} · {detail}";
                    return new RowItem(title, detail, "Call", number, Company: company, CompanyId: Value(call, "companyId"), CallId: Value(call, "callId"), Notes: notes, Outcome: outcome);
                }).ToList();
                return rows.Count > 0 ? rows : EmptyRows("No live call history is available yet. Completed and missed calls will appear here after real traffic is received.");
            }
        }
        catch { }
        return EmptyRows("Call history is temporarily unavailable. Restart CallBridge and try again.");
    }

    private async Task EditSelectedCallNoteAsync()
    {
        if (RowsList.SelectedItem is not RowItem row || string.IsNullOrWhiteSpace(row.CallId)) { ShowWarning("Select a call first."); return; }
        var editor = new CallNoteWindow(row.Title, row.Notes, row.Outcome) { Owner = this };
        if (editor.ShowDialog() != true) return;
        try
        {
            var json = JsonSerializer.Serialize(new { notes = editor.Notes, outcome = editor.Outcome }, JsonOptions());
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{_settings.ApiBase.TrimEnd('/')}/calls/{Uri.EscapeDataString(row.CallId)}/notes") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(await response.Content.ReadAsStringAsync());
            if (_listTitle == RecentCallsTitle) await NavigateToRowsAsync(RecentCallsTitle, "Your latest inbound, outbound, and missed calls", HistoryRowsAsync);
        }
        catch (Exception ex) { ShowWarning($"Could not save call notes: {ex.Message}"); }
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
                return rows.Count > 0 ? rows : EmptyRows("The directory is empty. Sync ConnectWise contacts or import a CSV in Settings.");
            }
        }
        catch
        {
            return EmptyRows("Contacts are temporarily unavailable. Restart CallBridge and try again.");
        }
        return EmptyRows("The directory is empty. Sync ConnectWise contacts or import a CSV in Settings.");
    }

    private static List<RowItem> EmptyRows(string message) => [new("No live data", message, "Open")];

    private VoicemailStatus _voicemail = VoicemailStatus.Unknown;
    private readonly Dictionary<string, ParkSlotState> _parkStates = new(StringComparer.Ordinal);
    private List<string> _parkSlots = [];

    private void WirePhoneFeatures()
    {
        _sip.VoicemailStatusChanged += status => Dispatcher.BeginInvoke(() => ApplyVoicemailStatus(status));
        _sip.ParkSlotStatusChanged += status => Dispatcher.BeginInvoke(() => ApplyParkSlotStatus(status));
        ParkButton.Click += (_, _) => ToggleParkPanel();
        CloseParkButton.Click += (_, _) => ParkPanel.Visibility = Visibility.Collapsed;
        SetControlMetadata(ParkButton, "Park call", "Park the caller in a park slot so anyone can pick up.");
        SetControlMetadata(CloseParkButton, "Close park options", "Hide the park options.");
        UpdateMoreTabBadge();
    }

    /// <summary>Applies saved voicemail and park settings to the phone.</summary>
    private void ApplyPhoneFeatureSettings()
    {
        _parkSlots = SipFeatureCodes.TryParseSlots(_settings.ParkSlots, out var slots, out _) ? slots : [];
        foreach (var stale in _parkStates.Keys.Except(_parkSlots).ToList()) _parkStates.Remove(stale);
        _sip.ConfigureParkSlots(_parkSlots);
        UpdateStatusCards();
        RefreshMoreRowsIfVisible();
    }

    private void ApplyVoicemailStatus(VoicemailStatus status)
    {
        _voicemail = status;
        UpdateMoreTabBadge();
        UpdateStatusCards();
        RefreshMoreRowsIfVisible();
    }

    private void ApplyParkSlotStatus(ParkSlotStatus status)
    {
        if (!_parkSlots.Contains(status.Slot)) return;
        _parkStates[status.Slot] = status.State;
        UpdateStatusCards();
        RefreshMoreRowsIfVisible();
        if (ParkPanel.Visibility == Visibility.Visible) BuildParkOptions();
    }

    private void RefreshMoreRowsIfVisible()
    {
        if (_listTitle == MoreListTitle && HomeView.Visibility == Visibility.Visible)
            ShowRows(MoreListTitle, MoreSubtitle, MoreRows());
    }

    private void UpdateMoreTabBadge()
    {
        var count = _voicemail.Known ? _voicemail.NewMessages : 0;
        if (count <= 0)
        {
            MoreTabButton.Content = "More";
            AutomationProperties.SetName(MoreTabButton, "More");
            return;
        }
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock { Text = "More", VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "Accent");
        badge.Child = new TextBlock { Text = count > 99 ? "99+" : count.ToString(), Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
        content.Children.Add(badge);
        MoreTabButton.Content = content;
        AutomationProperties.SetName(MoreTabButton, $"More, {count} new voicemail{(count == 1 ? "" : "s")}");
    }

    private const string MoreSubtitle = "Voicemail and call parking";
    private const string CallVoicemailAction = "Call voicemail";
    private const string PickUpAction = "Pick up";
    private const string SetUpAction = "Set up";

    private List<RowItem> MoreRows()
    {
        var rows = new List<RowItem>();
        var voicemailCode = _settings.VoicemailAccessCode.Trim();
        rows.Add(SipFeatureCodes.IsValidDialString(voicemailCode)
            ? new RowItem("Voicemail", _voicemail.Summary, CallVoicemailAction, voicemailCode)
            : new RowItem("Voicemail", "Add the voicemail access code in Settings > Phone account.", SetUpAction));

        if (_parkSlots.Count == 0)
        {
            rows.Add(new RowItem("Call parking", "Add park slots in Settings > Phone account to park and pick up calls.", SetUpAction));
            return rows;
        }
        var prefix = _settings.ParkPickupPrefix.Trim();
        foreach (var slot in _parkSlots)
        {
            var state = _parkStates.TryGetValue(slot, out var known) ? known : ParkSlotState.Unknown;
            rows.Add(new RowItem($"Park slot {slot}", new ParkSlotStatus(slot, state).Summary, PickUpAction, prefix + slot));
        }
        return rows;
    }

    private bool HasParkDestinations =>
        _parkSlots.Count > 0 || SipFeatureCodes.IsValidDialString(_settings.ParkCode);

    private void ToggleParkPanel()
    {
        var show = ParkPanel.Visibility != Visibility.Visible;
        TransferPanel.Visibility = Visibility.Collapsed;
        ParkPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) BuildParkOptions();
    }

    private void BuildParkOptions()
    {
        ParkSlotsPanel.Children.Clear();
        var parkCode = _settings.ParkCode.Trim();
        if (SipFeatureCodes.IsValidDialString(parkCode))
            ParkSlotsPanel.Children.Add(ParkOptionButton("Any open slot", parkCode, "Park in the next open slot", primary: true));
        foreach (var slot in _parkSlots)
        {
            var state = _parkStates.TryGetValue(slot, out var known) ? known : ParkSlotState.Unknown;
            var label = state == ParkSlotState.Occupied ? $"{slot} · in use" : slot;
            var button = ParkOptionButton(label, slot, $"Park on slot {slot}", primary: false);
            button.IsEnabled = state != ParkSlotState.Occupied;
            ParkSlotsPanel.Children.Add(button);
        }
        ParkPanelMessageText.Text = HasParkDestinations
            ? "The caller hears hold music until someone picks up from More or dials the slot."
            : "Ask your admin to add park slots or a park code in Settings > Phone account.";
    }

    private Button ParkOptionButton(string label, string destination, string tooltip, bool primary)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6), MinWidth = 64 };
        if (primary) button.Style = (Style)FindResource("PrimaryButton");
        SetControlMetadata(button, tooltip, tooltip);
        button.Click += async (_, _) => await ParkCallAsync(destination, label);
        return button;
    }

    private async Task ParkCallAsync(string destination, string label)
    {
        if (_activeCallId is null || !SipFeatureCodes.IsValidDialString(destination)) return;
        var result = await _sip.TransferAsync(destination);
        if (!result.Success)
        {
            ParkPanelMessageText.Text = $"The call couldn't be parked: {result.Message}";
            return;
        }
        ParkPanel.Visibility = Visibility.Collapsed;
        EndLocalCall("CALL PARKED");
        var pickup = _parkSlots.Contains(destination) ? $" Pick it up from More or dial {_settings.ParkPickupPrefix.Trim()}{destination}." : " The phone system announces the slot.";
        ShowBanner($"Call parked on {label}.{pickup}", GoodHex);
    }


    private void ShowSettings()
    {
        _viewRequestVersion++;
        SetActiveNav(SettingsButton);
        HomeView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        if (!_settingsDirty) LoadSettingsIntoView();
        ShowSettingsSection(_activeSettingsSection);
        SetSettingsStatus(_settingsDirty ? "Unsaved changes" : "No unsaved changes", _settingsDirty ? "warning" : "neutral");
        SettingsScroller.Focus();
    }

    private async Task CheckForUpdatesLoopAsync()
    {
        // Let startup, sign-in, and the directory load settle before the first check; then re-check a few times a day.
        await Task.Delay(TimeSpan.FromSeconds(30));
        while (IsLoaded)
        {
            await CheckForUpdatesAsync(manual: false);
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates || _installingUpdate) return;
        _checkingForUpdates = true;
        _lastUpdateError = null;
        RefreshUpdateIndicators();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var update = await UpdateChecker.CheckAsync(timeout.Token);
            _lastUpdateCheck = DateTimeOffset.Now;
            if (update is not null && (_availableUpdate is null || update.Version > _availableUpdate.Version))
            {
                _availableUpdate = update;
                ShowUpdateBanner();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or IOException)
        {
            App.LogStartup($"Update check skipped: {ex.GetType().Name}.");
            if (manual) _lastUpdateError = "Couldn't reach the update server. Check your internet connection and try again.";
        }
        finally
        {
            _checkingForUpdates = false;
            RefreshUpdateIndicators();
        }
    }

    private void RefreshUpdateIndicators()
    {
        var update = _availableUpdate;
        var busy = _checkingForUpdates || _installingUpdate;
        UpdateBadgeButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateBadgeButton.IsEnabled = !_installingUpdate;
        if (update is not null) UpdateBadgeButton.ToolTip = $"CallBridge {update.Version} is ready to install. Click to update.";

        AboutVersionText.Text = $"{Branding.ProductName} version {UpdateChecker.CurrentVersion}";
        CheckForUpdatesButton.IsEnabled = !busy;
        CheckForUpdatesButton.Content = _checkingForUpdates ? "Checking…" : "Check now";
        AboutInstallUpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        AboutInstallUpdateButton.IsEnabled = !busy;
        AboutInstallUpdateButton.Content = update is null ? "Install update" : $"Install {update.Version}";
        AboutUpdateStatusText.Text = _checkingForUpdates ? "Checking for updates…"
            : _installingUpdate ? "Installing the update…"
            : _lastUpdateError is not null ? _lastUpdateError
            : update is not null ? $"CallBridge {update.Version} is available."
            : _lastUpdateCheck is null ? "Not checked yet"
            : "You're up to date.";
        AboutUpdateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            _lastUpdateError is not null ? "Bad" : update is not null ? "Accent" : _lastUpdateCheck is null || busy ? "Muted" : "Good");
        AboutLastCheckedText.Text = _lastUpdateCheck is { } checkedAt ? $"Last checked {checkedAt:MMM d, h:mm tt}" : "";
    }

    private void OpenUpdatesFromTray()
    {
        OpenFromTray(showSettings: true);
        ShowSettingsSection("About");
        if (_availableUpdate is not null) _ = InstallUpdateAsync();
        else _ = CheckForUpdatesAsync(manual: true);
    }

    private void OpenReleaseNotes()
    {
        var page = _availableUpdate?.ReleasePage ?? new Uri($"https://github.com/{UpdateChecker.ReleasesRepository}/releases");
        try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception ex) { App.LogStartup($"Release notes couldn't open: {ex.GetType().Name}."); }
    }

    private void ShowUpdateBanner()
    {
        if (_availableUpdate is null) return;
        ShowBanner($"CallBridge {_availableUpdate.Version} is available. You have {UpdateChecker.CurrentVersion}.", ThemePalette.Hex("Accent"));
        InstallUpdateButton.Content = "Install update";
        InstallUpdateButton.IsEnabled = true;
        InstallUpdateButton.Visibility = Visibility.Visible;
    }
    private bool IsCallInProgress()
    {
        var sipStatus = _sip.CurrentCall;
        return _activeCallId is not null || _sipCallId is not null
            || sipStatus.HasPendingIncomingCall || _sip.CurrentWaitingCall.HasPendingCall
            || sipStatus.State is not (SipCallState.Idle or SipCallState.Failed);
    }

    private async Task InstallUpdateAsync()
    {
        if (_availableUpdate is not { } update || _installingUpdate) return;
        if (IsCallInProgress())
        {
            ShowBanner("Finish your call first, then install the update.", ThemePalette.Hex("Warn"));
            return;
        }

        _installingUpdate = true;
        InstallUpdateButton.IsEnabled = false;
        RefreshUpdateIndicators();
        try
        {
            var progress = new Progress<int>(percent => StatusText.Text = $"Downloading CallBridge {update.Version}… {percent}%");
            ShowBanner($"Downloading CallBridge {update.Version}…", ThemePalette.Hex("Accent"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            var msi = await UpdateChecker.DownloadVerifiedAsync(update, progress, timeout.Token);
            if (IsCallInProgress())
            {
                ShowBanner("The update is ready. Finish your call, then click Install update.", ThemePalette.Hex("Warn"));
                InstallUpdateButton.IsEnabled = true;
                return;
            }
            ShowBanner($"Installing CallBridge {update.Version}. CallBridge will close and reopen from the Start menu.", ThemePalette.Hex("Accent"));
            UpdateChecker.LaunchInstaller(msi);
            App.LogStartup($"Update to {update.Version} started.");
            await Task.Delay(TimeSpan.FromSeconds(2));
            _exitRequested = true;
            System.Windows.Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The admin prompt was declined.
            ShowUpdateBanner();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ShowBanner(ex is InvalidDataException ? ex.Message : "The update couldn't be downloaded. Try again later.", ThemePalette.Hex("Bad"));
            InstallUpdateButton.IsEnabled = true;
        }
        finally
        {
            _installingUpdate = false;
            RefreshUpdateIndicators();
        }
    }
    private void ShowBanner(string message, string dotHex)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        StatusText.Text = message;
        StatusDot.Fill = Brush(dotHex);
        StatusBanner.Visibility = Visibility.Visible;
    }

    internal static string LogoInitials(string productName)
    {
        var capitals = productName.Where(char.IsUpper).Take(2).ToArray();
        if (capitals.Length == 2) return new string(capitals);
        var letters = productName.Where(char.IsLetterOrDigit).Take(2).ToArray();
        return letters.Length == 0 ? "CB" : new string(letters).ToUpperInvariant();
    }

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string FormatCallTime(string value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time)
            ? time.ToLocalTime().ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.CurrentCulture)
            : value;

    private const string ActiveTabTag = "Active";
    private const string RecentCallsTitle = "Recent calls";
    private const string ContactsListTitle = "Contacts";
    private const string MoreListTitle = "More";
    private static string AccentHex => ThemePalette.Hex("Accent");
    private static string GoodHex => ThemePalette.Hex("Good");
    private static string WarnHex => ThemePalette.Hex("Warn");
    private static string BadHex => ThemePalette.Hex("Bad");
    private static string MutedHex => ThemePalette.Hex("Muted");

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}

/// <summary>A ticket the in-call notes can be logged to. <see cref="None"/> means no ticket.</summary>
public sealed record CallTicketChoice(string Id, string Label)
{
    public static readonly CallTicketChoice None = new("", "No ticket");
}

public sealed record RowItem(string Title, string Detail, string Action, string Destination = "", string ContactId = "", string Company = "", string CompanyId = "", string CallId = "", string Notes = "", string Outcome = "")
{
    public override string ToString() => $"{Title}\n{Detail}    {Action}";
}

public sealed record TelephonyResult(bool Success, string? CallId, string Message);

public static class LocalApi
{
    public const string DefaultBase = "http://127.0.0.1:8787";
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
    [JsonIgnore]
    public string ApiBase { get; set; } = LocalApi.DefaultBase;
    public string Extension { get; set; } = "201";
    public string CallerId { get; set; } = "201";
    public string Provider { get; set; } = "Standard SIP";
    public string SipServer { get; set; } = "";
    public string SipUsername { get; set; } = "";
    [JsonIgnore]
    public string SipPassword { get; set; } = "";
    public string SipPasswordProtected { get; set; } = "";
    // New installs default to TLS. Stored values are honoured as-is: some PBXs (HivePBX extensions, for example) accept only UDP.
    public string SipTransport { get; set; } = "TLS";
    public string SipCodecProfileName { get; set; } = global::CallBridge.Desktop.SipCodecProfile.CompatibilityName;
    public string Microphone { get; set; } = WindowsAudioDeviceCatalog.DefaultDeviceId;
    public string Speaker { get; set; } = WindowsAudioDeviceCatalog.DefaultDeviceId;
    public string RingtoneSpeaker { get; set; } = WindowsAudioDeviceCatalog.DefaultDeviceId;
    public bool RegistrationEnabled { get; set; } = true;
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
    public string ConnectWiseTicketingMode { get; set; } = CallBridge.Desktop.ConnectWiseTicketingMode.Psa;
    public string ConnectWisePlatformBoardId { get; set; } = "";
    public string ConnectWisePlatformBoardName { get; set; } = "";
    public string ConnectWisePlatformSourceId { get; set; } = "";
    public string ConnectWisePlatformSourceName { get; set; } = "";
    public string AdminPinHash { get; set; } = "";
    public string AdminPinSalt { get; set; } = "";
    public string BrandAccentColor { get; set; } = "";
    public string BrandLogoPath { get; set; } = "";
    public string VoicemailAccessCode { get; set; } = "*97";
    public string ParkSlots { get; set; } = "";
    public string ParkCode { get; set; } = "";
    public string ParkPickupPrefix { get; set; } = "";
    public string ConnectWiseMemberIdentifier { get; set; } = "";
    public string ConnectWisePlatformBaseUrl { get; set; } = ConnectWisePlatformClient.NorthAmericaBaseUrl;
    public string ConnectWisePlatformClientId { get; set; } = "";
    [JsonIgnore]
    public string ConnectWisePlatformClientSecret { get; set; } = "";
    public string ConnectWisePlatformClientSecretProtected { get; set; } = "";
    public string ConnectWisePlatformScopes { get; set; } = ConnectWisePlatformClient.TicketingScopes;
    [JsonIgnore]
    public string ConnectWisePlatformAccessToken { get; set; } = "";
    public string ConnectWisePlatformAccessTokenProtected { get; set; } = "";
    public DateTimeOffset? ConnectWisePlatformAccessTokenExpiresAt { get; set; }
}

internal static class DialogControlMetadata
{
    public static void Apply(Button button)
    {
        var label = button.Content?.ToString() ?? "Action";
        Apply(button, label);
    }

    public static void Apply(FrameworkElement element, string label)
    {
        element.ToolTip = label;
        ToolTipService.SetShowOnDisabled(element, true);
        AutomationProperties.SetName(element, label);
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
        Title = "Call notes"; Width = 520; Height = 390; MinWidth = 420; MinHeight = 330; ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ThemeWindows.ApplyDialog(this);
        _notes.Text = notes;
        foreach (var item in new[] { "", "Resolved", "Follow-up required", "Escalated", "Ticket created", "No answer" }) _outcome.Items.Add(item);
        _outcome.SelectedItem = _outcome.Items.Cast<string>().FirstOrDefault(x => x == outcome) ?? "";
        var stack = new StackPanel { Margin = new Thickness(26) };
        stack.Children.Add(new TextBlock { Text = $"Notes for {number}", TextWrapping = TextWrapping.Wrap, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        AddField(stack, "Outcome", _outcome);
        AddField(stack, "Technician notes", _notes);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = "Save notes", IsDefault = true, Padding = new Thickness(16, 9, 16, 9), HorizontalAlignment = HorizontalAlignment.Right, Background = ThemePalette.Brush("Accent"), Foreground = Brushes.White, Margin = new Thickness(0, 16, 0, 0) };
        save.Margin = new Thickness(0);
        save.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        foreach (var button in buttons.Children.OfType<Button>()) DialogControlMetadata.Apply(button);
        stack.Children.Add(buttons); Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => _outcome.Focus();
    }

    private static void AddField(Panel panel, string label, Control field)
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = ThemePalette.Brush("Muted"), Margin = new Thickness(0, 6, 0, 5) });
        DialogControlMetadata.Apply(field, label);
        panel.Children.Add(field);
    }
}

public sealed class TicketWindow : Window
{
    private readonly TextBox _summary = new() { Padding = new Thickness(9) };
    private readonly TextBox _description = new() { Padding = new Thickness(9), Height = 130, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _companySearch = new() { Padding = new Thickness(9) };
    private readonly ListBox _companyResults = new() { MaxHeight = 140, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _companyStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Func<string, Task<IReadOnlyList<ConnectWiseCompanySummary>>>? _searchCompanies;
    private string _companyId;
    private string _companyName;

    public string Summary => _summary.Text.Trim();
    public string Description => _description.Text.Trim();
    public string CompanyId => _companyId;
    public string CompanyName => _companyName;

    /// <summary>
    /// Creates the ticket dialog. When <paramref name="companyId"/> isn't a ConnectWise company ID and a search
    /// function is supplied, the dialog lets the technician find and choose the company.
    /// </summary>
    public TicketWindow(string company, string contact, string phone, string companyId = "", Func<string, Task<IReadOnlyList<ConnectWiseCompanySummary>>>? searchCompanies = null)
    {
        _companyId = ConnectWiseClient.IsCompanyId(companyId) ? companyId : "";
        _companyName = _companyId.Length > 0 ? company : "";
        _searchCompanies = searchCompanies;
        var needsCompany = _companyId.Length == 0 && searchCompanies is not null;

        Title = "New ConnectWise ticket"; Width = 540; Height = needsCompany ? 560 : 390; MinWidth = 430; MinHeight = 330; ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeWindows.ApplyDialog(this);
        _summary.Text = string.IsNullOrWhiteSpace(company) ? "Phone support" : $"Phone support - {company}";
        _description.Text = $"Caller: {contact}\nPhone: {phone}\n\nIssue and troubleshooting notes:\n";
        var stack = new StackPanel { Margin = new Thickness(26) };
        stack.Children.Add(new TextBlock { Text = "Create service ticket", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        var subtitle = string.Join(" - ", new[] { company, contact }.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (subtitle.Length > 0)
            stack.Children.Add(new TextBlock { Text = subtitle, TextWrapping = TextWrapping.Wrap, Foreground = ThemePalette.Brush("Muted"), Margin = new Thickness(0, 0, 0, 14) });

        if (needsCompany)
        {
            var searchRow = new Grid();
            searchRow.ColumnDefinitions.Add(new ColumnDefinition());
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchRow.Children.Add(_companySearch);
            var search = new Button { Content = "Search", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(search, 1);
            searchRow.Children.Add(search);
            AddLabel(stack, "Company");
            DialogControlMetadata.Apply(_companySearch, "Company search");
            DialogControlMetadata.Apply(_companyResults, "Matching companies");
            DialogControlMetadata.Apply(search);
            stack.Children.Add(searchRow);
            stack.Children.Add(_companyResults);
            _companyStatus.Foreground = ThemePalette.Brush("Muted");
            _companyStatus.Text = "This caller isn't matched to a ConnectWise company. Search by company name.";
            stack.Children.Add(_companyStatus);
            _companySearch.Text = company;
            search.Click += async (_, _) => await SearchAsync();
            _companySearch.KeyDown += async (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                e.Handled = true;
                await SearchAsync();
            };
            _companyResults.SelectionChanged += (_, _) =>
            {
                if (_companyResults.SelectedItem is not ConnectWiseCompanySummary chosen) return;
                _companyId = chosen.Id;
                _companyName = chosen.Name;
                _companyStatus.Text = $"Ticket will be created for {chosen.Name}.";
            };
        }

        AddField(stack, "Summary", _summary); AddField(stack, "Description", _description);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(0, 0, 10, 0) };
        var create = new Button { Content = "Create ticket", IsDefault = true, Padding = new Thickness(16, 9, 16, 9), HorizontalAlignment = HorizontalAlignment.Right, Background = ThemePalette.Brush("Accent"), Foreground = Brushes.White };
        create.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(Summary)) { MessageBox.Show(this, "Summary is required.", "CallBridge", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            if (_searchCompanies is not null && !ConnectWiseClient.IsCompanyId(_companyId))
            {
                MessageBox.Show(this, "Choose the ConnectWise company for this ticket.", "CallBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
                _companySearch.Focus();
                return;
            }
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        foreach (var button in buttons.Children.OfType<Button>()) DialogControlMetadata.Apply(button);
        stack.Children.Add(buttons); Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) =>
        {
            if (needsCompany) _companySearch.Focus();
            else _summary.Focus();
        };
    }

    private async Task SearchAsync()
    {
        if (_searchCompanies is null) return;
        var query = _companySearch.Text.Trim();
        if (query.Length == 0) { _companyStatus.Text = "Enter part of the company name."; return; }
        _companyStatus.Text = "Searching ConnectWise…";
        try
        {
            var results = await _searchCompanies(query);
            _companyResults.ItemsSource = results;
            _companyStatus.Text = results.Count == 0 ? $"No active companies start with \"{query}\"." : "Select the company for this ticket.";
            if (results.Count == 1) _companyResults.SelectedIndex = 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidDataException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _companyStatus.Text = $"Company search failed: {ex.Message}";
        }
    }

    private static void AddLabel(Panel panel, string label) =>
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = ThemePalette.Brush("Muted"), Margin = new Thickness(0, 6, 0, 5) });

    private static void AddField(Panel panel, string label, Control field) { AddLabel(panel, label); DialogControlMetadata.Apply(field, label); panel.Children.Add(field); }
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
        Width = 440; Height = 330; MinWidth = 380; MinHeight = 300; ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeWindows.ApplyDialog(this);
        _contact.Text = contact; _company.Text = company; _phone.Text = phone;
        var stack = new StackPanel { Margin = new Thickness(26) };
        stack.Children.Add(new TextBlock { Text = Title, TextWrapping = TextWrapping.Wrap, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        AddField(stack, "Contact name", _contact); AddField(stack, "Company", _company); AddField(stack, "Phone or extension", _phone);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(0, 0, 10, 0) };
        var save = new Button { Content = "Save contact", IsDefault = true, Padding = new Thickness(16, 9, 16, 9), HorizontalAlignment = HorizontalAlignment.Right, Background = ThemePalette.Brush("Accent"), Foreground = Brushes.White, Margin = new Thickness(0, 16, 0, 0) };
        save.Margin = new Thickness(0);
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ContactName) || string.IsNullOrWhiteSpace(CompanyName) || string.IsNullOrWhiteSpace(Phone)) { MessageBox.Show(this, "Contact, company, and phone are required.", "CallBridge", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        foreach (var button in buttons.Children.OfType<Button>()) DialogControlMetadata.Apply(button);
        stack.Children.Add(buttons); Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => _contact.Focus();
    }
    private static void AddField(Panel panel, string label, Control field)
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = ThemePalette.Brush("Muted"), Margin = new Thickness(0, 6, 0, 5) });
        DialogControlMetadata.Apply(field, label);
        panel.Children.Add(field);
    }
}

public sealed class PromptWindow : Window
{
    private readonly TextBox _value = new() { Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(8) };
    public string Value => _value.Text;

    public PromptWindow(string title, string label)
    {
        ThemeWindows.ApplyDialog(this);
        Title = title;
        Width = 360;
        Height = 180;
        MinWidth = 320;
        MinHeight = 170;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        DialogControlMetadata.Apply(_value, label);
        stack.Children.Add(_value);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(12, 7, 12, 7), HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        foreach (var button in buttons.Children.OfType<Button>()) DialogControlMetadata.Apply(button);
        stack.Children.Add(buttons);
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => _value.Focus();
    }
}






