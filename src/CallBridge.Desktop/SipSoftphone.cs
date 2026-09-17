using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;

namespace CallBridge.Desktop;

public sealed record SipOperationResult(bool Success, string Message);

public sealed class SipSoftphone : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SipCallStateMachine _callState = new();
    private readonly SipTransferStateMachine _transferState = new();
    private readonly SipCallWaitingStateMachine _waitingState = new();
    private readonly SemaphoreSlim _transferGate = new(1, 1);
    private readonly SemaphoreSlim _callWaitingGate = new(1, 1);
    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _registration;
    private SIPUserAgent? _userAgent;
    private SIPServerUserAgent? _pendingIncomingCall;
    private WindowsAudioEndPoint? _audio;
    private RTPSession? _media;
    private SipCallQualityMonitor? _qualityMonitor;
    private SIPUserAgent? _waitingAgent;
    private SIPServerUserAgent? _waitingIncomingCall;
    private WindowsAudioEndPoint? _waitingAudio;
    private RTPSession? _waitingMedia;
    private SipCallQualityMonitor? _waitingQualityMonitor;
    private bool _activeCallIsWaiting;
    private string _primaryRemoteParty = "";
    private string _primaryRemoteNumber = "";
    private SipCallDirection _primaryDirection = SipCallDirection.None;
    private string _primaryCallId = "";
    private string _waitingRemoteParty = "";
    private string _waitingRemoteNumber = "";
    private string _waitingCallId = "";
    private readonly HashSet<string> _terminalCallIds = [];
    private readonly Queue<string> _terminalCallOrder = [];
    private bool _isMuted;
    private SIPUserAgent? _consultationAgent;
    private WindowsAudioEndPoint? _consultationAudio;
    private RTPSession? _consultationMedia;
    private SipCallQualityMonitor? _consultationQualityMonitor;
    private bool _consultationEnding;
    private string _server = "";
    private string _username = "";
    private string _password = "";
    private SipSignalingTransport _signalingTransport = SipSignalingTransport.Udp;
    private string _microphoneDeviceId = WindowsAudioDeviceCatalog.DefaultDeviceId;
    private string _speakerDeviceId = WindowsAudioDeviceCatalog.DefaultDeviceId;
    private string _codecProfileName = SipCodecProfile.CompatibilityName;

    public bool IsRegistered { get; private set; }

    private SIPNotifierClient? _voicemailSubscription;
    private readonly Dictionary<string, SIPNotifierClient> _parkSubscriptions = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _parkSlots = [];

    /// <summary>Latest voicemail counts reported by the phone system.</summary>
    public VoicemailStatus Voicemail { get; private set; } = VoicemailStatus.Unknown;

    public event Action<VoicemailStatus>? VoicemailStatusChanged;
    public event Action<ParkSlotStatus>? ParkSlotStatusChanged;

    /// <summary>Sets the park slots to watch. Subscriptions restart if the phone is registered.</summary>
    public void ConfigureParkSlots(IReadOnlyList<string> slots)
    {
        _parkSlots = slots.Where(SipFeatureCodes.IsValidDialString).Distinct(StringComparer.Ordinal).Take(20).ToList();
        if (!IsRegistered || _transport is null) return;
        StopParkSubscriptions();
        StartParkSubscriptions();
    }

    private void StartFeatureSubscriptions()
    {
        if (_transport is null || string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_server)) return;
        if (_voicemailSubscription is null)
        {
            try
            {
                var mailbox = SipTransportProfile.BuildDestinationUri(_username, _server, _signalingTransport);
                var subscription = new SIPNotifierClient(_transport, null, SIPEventPackagesEnum.MessageSummary, mailbox, _username, _server, _password, 3600, null);
                subscription.NotificationReceived += (_, body) => ApplyVoicemailNotification(body);
                subscription.Start();
                _voicemailSubscription = subscription;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
            {
                AudioStatusChanged?.Invoke("Voicemail status isn't available from this phone system.");
            }
        }
        if (_parkSubscriptions.Count == 0) StartParkSubscriptions();
    }

    private void StartParkSubscriptions()
    {
        if (_transport is null) return;
        foreach (var slot in _parkSlots)
        {
            try
            {
                var slotUri = SipTransportProfile.BuildDestinationUri(slot, _server, _signalingTransport);
                var subscription = new SIPNotifierClient(_transport, null, SIPEventPackagesEnum.Dialog, slotUri, _username, _server, _password, 3600, null);
                subscription.NotificationReceived += (_, body) => ParkSlotStatusChanged?.Invoke(new ParkSlotStatus(slot, ParkSlotStatus.ParseDialogInfo(body)));
                subscription.SubscriptionFailed += (_, _, _) => ParkSlotStatusChanged?.Invoke(new ParkSlotStatus(slot, ParkSlotState.Unknown));
                subscription.Start();
                _parkSubscriptions[slot] = subscription;
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
            {
                ParkSlotStatusChanged?.Invoke(new ParkSlotStatus(slot, ParkSlotState.Unknown));
            }
        }
    }

    private void StopParkSubscriptions()
    {
        foreach (var subscription in _parkSubscriptions.Values)
        {
            try { subscription.Stop(); } catch { }
        }
        _parkSubscriptions.Clear();
    }

    private void StopFeatureSubscriptions()
    {
        try { _voicemailSubscription?.Stop(); } catch { }
        _voicemailSubscription = null;
        StopParkSubscriptions();
    }

    private void ApplyVoicemailNotification(string? body)
    {
        if (VoicemailStatus.Parse(body) is not { } status) return;
        Voicemail = status;
        VoicemailStatusChanged?.Invoke(status);
    }

    /// <summary>
    /// Accepts message-summary NOTIFY requests the phone system sends without a subscription (unsolicited MWI).
    /// Notifications that belong to CallBridge subscriptions are left to their subscription clients.
    /// </summary>
    private async Task<bool> TryHandleUnsolicitedNotifyAsync(SIPRequest request)
    {
        if (request.Method != SIPMethodsEnum.NOTIFY || _transport is null) return false;
        var callId = request.Header.CallId;
        if (string.Equals(_voicemailSubscription?.CallID, callId, StringComparison.Ordinal)
            || _parkSubscriptions.Values.Any(subscription => string.Equals(subscription.CallID, callId, StringComparison.Ordinal)))
            return true;
        if (!string.Equals(request.Header.Event?.Split(';')[0].Trim(), "message-summary", StringComparison.OrdinalIgnoreCase)) return false;
        ApplyVoicemailNotification(request.Body);
        try { await _transport.SendResponseAsync(SIPResponse.GetResponse(request, SIPResponseStatusCodesEnum.Ok, null)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
        return true;
    }
    public bool IsRegistrationRunning => _registration is not null;
    public bool IsCallActive => _callState.Current.HasEstablishedCall;
    public SipCallStatus CurrentCall => _callState.Current;
    public SipTransferStatus CurrentTransfer => _transferState.Current;
    public SipCallWaitingStatus CurrentWaitingCall => _waitingState.Current;
    public SipCallQualitySnapshot CurrentQuality =>
        (_transferState.Current.IsActive
            ? _consultationQualityMonitor
            : _activeCallIsWaiting ? _waitingQualityMonitor : _qualityMonitor)?.Current
        ?? SipCallQualitySnapshot.Empty;
    public event Action<string>? RegistrationStateChanged;
    public event Action<string>? AudioStatusChanged;
    public event Action<SipCallQualitySnapshot>? CallQualityChanged;
    public event Action<SipCallLifecycleEvent>? CallLifecycleChanged;
    public event Action<SipCallStatus>? CallStateChanged
    {
        add => _callState.Changed += value;
        remove => _callState.Changed -= value;
    }
    public event Action<SipTransferStatus>? TransferStateChanged
    {
        add => _transferState.Changed += value;
        remove => _transferState.Changed -= value;
    }
    public event Action<SipCallWaitingStatus>? CallWaitingStateChanged
    {
        add => _waitingState.Changed += value;
        remove => _waitingState.Changed -= value;
    }

    public void ConfigureAudioDevices(string? microphoneDeviceId, string? speakerDeviceId)
    {
        lock (_sync)
        {
            _microphoneDeviceId = string.IsNullOrWhiteSpace(microphoneDeviceId)
                ? WindowsAudioDeviceCatalog.DefaultDeviceId
                : microphoneDeviceId;
            _speakerDeviceId = string.IsNullOrWhiteSpace(speakerDeviceId)
                ? WindowsAudioDeviceCatalog.DefaultDeviceId
                : speakerDeviceId;
        }
    }

    public void ConfigureCodecProfile(string? codecProfileName)
    {
        lock (_sync) _codecProfileName = SipCodecProfile.Resolve(codecProfileName).Name;
    }

    public async Task<SipOperationResult> RegisterAsync(string server, string username, string password, string transport)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new(false, "SIP server, username, and password are required.");
        if (!SipTransportProfile.TryParse(transport, out var signalingTransport))
            return new(false, "Choose UDP, TCP, or TLS for SIP transport.");
        signalingTransport = SipTransportProfile.ResolveTransport(server, signalingTransport);

        SIPURI registrarUri;
        try { registrarUri = SipTransportProfile.BuildRegistrarUri(server, signalingTransport); }
        catch (FormatException ex) { return new(false, SafeText(ex.Message, "The SIP server is invalid.")); }

        await ShutdownAsync();
        _server = registrarUri.Host;
        _username = username.Trim();
        _password = password;
        _signalingTransport = signalingTransport;
        _transport = new SIPTransport();
        _transport.AddSIPChannel(SipTransportProfile.CreateChannel(signalingTransport));
        _userAgent = CreateCallAgent(_transport);
        _transport.SIPTransportRequestReceived += HandleTransportRequestAsync;

        var completion = new TaskCompletionSource<SipOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registration = new SIPRegistrationUserAgent(_transport, _username, _password, registrarUri.ToString(), 300);
        _registration.RegistrationSuccessful += (_, _) =>
        {
            IsRegistered = true;
            RegistrationStateChanged?.Invoke("registered");
            StartFeatureSubscriptions();
            completion.TrySetResult(new(true, "Registered"));
        };
        _registration.RegistrationFailed += (_, _, error) =>
        {
            IsRegistered = false;
            RegistrationStateChanged?.Invoke($"failed: {error}");
            completion.TrySetResult(new(false, error));
        };
        _registration.RegistrationTemporaryFailure += (_, _, error) => RegistrationStateChanged?.Invoke($"retrying: {error}");
        _registration.RegistrationRemoved += (_, _) => { IsRegistered = false; RegistrationStateChanged?.Invoke("unregistered"); };
        _registration.Start();

        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) { return new(false, "SIP registration timed out. Verify the server, account, firewall, and transport requirements."); }
    }

    public async Task UnregisterAsync()
    {
        if (_registration is not null) _registration.Stop(true);
        IsRegistered = false;
        RegistrationStateChanged?.Invoke("unregistered");
        await ShutdownAsync();
    }

    public async Task<SipOperationResult> DialAsync(string destination)
    {
        if (!IsRegistered || _userAgent is null) return new(false, "Register the SIP account before dialing.");
        if (string.IsNullOrWhiteSpace(destination)) return new(false, "Enter a destination.");
        if (_callState.Current.State is not (SipCallState.Idle or SipCallState.Failed)) return new(false, "A call is already in progress.");
        var remoteParty = SafeText(destination, "Unknown destination");
        _primaryDirection = SipCallDirection.Outbound;
        _primaryRemoteParty = remoteParty;
        _primaryRemoteNumber = remoteParty;
        _primaryCallId = Guid.NewGuid().ToString("N");
        _isMuted = false;
        if (!SetCallState(SipCallState.OutboundDialing, SipCallDirection.Outbound, remoteParty, "Calling", remoteParty))
        {
            _primaryCallId = "";
            return new(false, "The phone is busy with another call.");
        }
        PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Started);
        try
        {
            _isMuted = false;
            _audio = CreateAudioEndpoint();
            _media = CreateMediaSession(_audio, SipMediaSlot.Primary);
            var target = SipTransportProfile.BuildDestinationUri(destination, _server, _signalingTransport);
            var answered = await _userAgent.Call(target.ToString(), _username, _password, _media, 30);
            if (answered)
            {
                SetCallState(SipCallState.Connected, detail: "Connected");
                PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Answered);
                return new(true, "Connected");
            }

            await CompleteFailedCallAsync("The SIP server did not complete the call.");
            return new(false, "The SIP server did not complete the call.");
        }
        catch (Exception ex)
        {
            var message = SafeText(ex.Message, "The call could not be completed.");
            await CompleteFailedCallAsync(message);
            return new(false, $"SIP call failed: {message}");
        }
    }

    public async Task<SipOperationResult> AnswerIncomingAsync()
    {
        if (_waitingState.Current.CanAnswer) return await AnswerWaitingCallAsync();

        SIPServerUserAgent? pending;
        lock (_sync)
        {
            pending = _pendingIncomingCall;
            if (pending is not null) _pendingIncomingCall = null;
        }

        if (pending is null || _userAgent is null || _callState.Current.State != SipCallState.IncomingRinging)
            return new(false, "There is no incoming call to answer.");
        if (!SetCallState(SipCallState.Connecting, detail: "Connecting"))
            return new(false, "The incoming call is no longer available.");

        try
        {
            _audio = CreateAudioEndpoint();
            _media = CreateMediaSession(_audio, SipMediaSlot.Primary);
            var answered = await _userAgent.Answer(pending, _media);
            if (!answered)
            {
                await CompleteFailedCallAsync("The incoming call could not be answered.");
                return new(false, "The incoming call could not be answered.");
            }

            SetCallState(SipCallState.Connected, detail: "Connected");
            PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Answered);
            return new(true, "Connected");
        }
        catch (Exception ex)
        {
            var message = SafeText(ex.Message, "The incoming call could not be answered.");
            await CompleteFailedCallAsync(message);
            return new(false, message);
        }
    }

    public async Task RejectIncomingAsync()
    {
        if (_waitingState.Current.CanDecline)
        {
            await RejectWaitingCallAsync();
            return;
        }

        SIPServerUserAgent? pending;
        lock (_sync)
        {
            pending = _pendingIncomingCall;
            _pendingIncomingCall = null;
        }

        if (pending is null) return;
        PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Declined);
        SetCallState(SipCallState.Ending, detail: "Declining call");
        try { pending.Reject(SIPResponseStatusCodesEnum.Decline, "Call declined"); } catch { }
        await CloseMediaAsync();
        _primaryCallId = "";
        _isMuted = false;
        SetCallState(SipCallState.Idle, detail: "Ready");
    }

    public async Task HangupAsync()
    {
        if (_waitingState.Current.HasTwoCalls)
        {
            await EndActiveCallWithWaitingAsync();
            return;
        }

        var state = _callState.Current.State;
        if (state == SipCallState.IncomingRinging)
        {
            await RejectIncomingAsync();
            return;
        }

        await StopConsultationAsync(false, true);
        PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Hangup);
        SetCallState(SipCallState.Ending, detail: "Ending call");
        if (_userAgent?.IsCalling == true || _userAgent?.IsRinging == true) _userAgent.Cancel();
        else if (_userAgent?.IsCallActive == true) _userAgent.Hangup();
        await CloseMediaAsync();
        _primaryCallId = "";
        _isMuted = false;
        SetCallState(SipCallState.Idle, detail: "Ready");
    }

    public async Task SetHoldAsync(bool hold)
    {
        if (_waitingState.Current.HasTwoCalls) throw new InvalidOperationException("Use Switch to move between the two calls.");
        var userAgent = ActiveUserAgent;
        var audio = ActiveAudio;
        if (userAgent?.IsCallActive != true) throw new InvalidOperationException("No active SIP call.");
        if (_transferState.Current.IsActive) throw new InvalidOperationException("Complete or cancel the consultation before changing hold state.");
        if (hold)
        {
            await HoldLegAsync(userAgent, audio);
            SetCallState(SipCallState.Held, detail: "On hold");
            PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Hold);
        }
        else
        {
            await ResumeLegAsync(userAgent, audio);
            SetCallState(SipCallState.Connected, detail: "Connected");
            PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Resume);
        }
    }

    public async Task SetMutedAsync(bool muted)
    {
        var audio = _transferState.Current.IsActive ? _consultationAudio : ActiveAudio;
        if (audio is null) throw new InvalidOperationException("No active SIP audio session.");
        _isMuted = muted;
        if (muted) await audio.PauseAudio(); else await audio.ResumeAudio();
    }

    public async Task SendDtmfAsync(string digits)
    {
        var userAgent = _transferState.Current.IsActive ? _consultationAgent : ActiveUserAgent;
        if (userAgent?.IsCallActive != true) throw new InvalidOperationException("No active SIP call.");
        foreach (var digit in digits)
        {
            var tone = digit switch { >= '0' and <= '9' => (byte)(digit - '0'), '*' => (byte)10, '#' => (byte)11, _ => throw new ArgumentException("DTMF supports 0-9, *, and #.") };
            await userAgent.SendDtmf(tone);
        }
    }

    public async Task<SipOperationResult> TransferAsync(string destination)
    {
        if (_waitingState.Current.HasTwoCalls) return new(false, "End one call before transferring the other call.");
        if (_userAgent?.IsCallActive != true) return new(false, "No active SIP call.");
        if (_transferState.Current.IsActive) return new(false, "Complete or cancel the current consultation first.");
        SIPURI target;
        try { target = SipTransportProfile.BuildDestinationUri(destination, _server, _signalingTransport); }
        catch (FormatException ex) { return new(false, SafeText(ex.Message, "The transfer destination is invalid.")); }
        var result = await _userAgent.BlindTransfer(target, TimeSpan.FromSeconds(15), CancellationToken.None);
        if (!result) return new(false, "The SIP transfer was rejected.");
        PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Transfer);
        SetCallState(SipCallState.Ending, detail: "Transfer complete");
        try { if (_userAgent.IsCallActive) _userAgent.Hangup(); } catch { }
        await CloseMediaAsync();
        _primaryCallId = "";
        _isMuted = false;
        SetCallState(SipCallState.Idle, detail: "Ready");
        return new(true, "Transferred");
    }

    public async Task<SipOperationResult> BeginAttendedTransferAsync(string destination)
    {
        await _transferGate.WaitAsync();
        try
        {
            if (_waitingState.Current.HasTwoCalls) return new(false, "End one call before starting a consultation transfer.");
            if (_userAgent?.IsCallActive != true || _transport is null) return new(false, "No active SIP call.");
            if (_transferState.Current.IsActive) return new(false, "A consultation is already in progress.");
            SIPURI target;
            try { target = SipTransportProfile.BuildDestinationUri(destination, _server, _signalingTransport); }
            catch (FormatException ex) { return new(false, SafeText(ex.Message, "The consultation destination is invalid.")); }

            var safeDestination = SafeText(destination, "Consultation");
            if (_transferState.Current.State == SipTransferState.Failed)
                _transferState.TryTransition(SipTransferState.None, detail: "Ready");
            _transferState.TryTransition(SipTransferState.ConsultationDialing, safeDestination, "Calling consultation");
            await HoldLegAsync(_userAgent, _audio);
            SetCallState(SipCallState.Held, detail: "Primary call on hold");

            _consultationAgent = new SIPUserAgent(_transport, null);
            _consultationAgent.ClientCallRinging += (_, response) => _transferState.TryTransition(
                SipTransferState.ConsultationRinging,
                detail: SipCallProgress.DescribeProvisionalResponse((int)response.Status, !string.IsNullOrWhiteSpace(response.Body)));
            _consultationAgent.ClientCallAnswered += (_, _) => _transferState.TryTransition(SipTransferState.ConsultationConnected, detail: "Consultation connected");
            _consultationAgent.ClientCallFailed += (_, error, _) => _transferState.TryTransition(SipTransferState.Failed, detail: SafeText(error, "Consultation failed"));
            _consultationAgent.OnCallHungup += dialogue =>
            {
                if (!_consultationEnding) _ = HandleConsultationEndedAsync("The consultation call ended.");
            };
            _consultationAudio = CreateAudioEndpoint();
            _consultationMedia = CreateMediaSession(_consultationAudio, SipMediaSlot.Consultation);

            var answered = await _consultationAgent.Call(target.ToString(), _username, _password, _consultationMedia, 30);
            if (!answered)
            {
                _transferState.TryTransition(SipTransferState.Failed, detail: "The consultation call was not answered.");
                await CloseConsultationCoreAsync(true, false);
                return new(false, "The consultation call was not answered. The original call has resumed.");
            }

            if (_isMuted && _consultationAudio is not null)
                await _consultationAudio.PauseAudio();
            _transferState.TryTransition(SipTransferState.ConsultationConnected, detail: "Consultation connected");
            return new(true, "Consultation connected. Complete or cancel the transfer.");
        }
        catch (Exception ex)
        {
            _transferState.TryTransition(SipTransferState.Failed, detail: "Consultation failed");
            await CloseConsultationCoreAsync(true, false);
            return new(false, SafeText(ex.Message, "The consultation could not be started."));
        }
        finally
        {
            _transferGate.Release();
        }
    }

    public async Task<SipOperationResult> CompleteAttendedTransferAsync()
    {
        await _transferGate.WaitAsync();
        try
        {
            if (!_transferState.Current.CanComplete || _userAgent?.IsCallActive != true || _consultationAgent?.Dialogue is null)
                return new(false, "There is no connected consultation to transfer.");
            _transferState.TryTransition(SipTransferState.Completing, detail: "Completing transfer");
            var transferred = await _userAgent.AttendedTransfer(
                _consultationAgent.Dialogue,
                TimeSpan.FromSeconds(15),
                CancellationToken.None);
            if (!transferred)
            {
                _transferState.TryTransition(SipTransferState.ConsultationConnected, detail: "Transfer was rejected");
                return new(false, "The SIP transfer was rejected. The consultation remains connected.");
            }

            PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Transfer);
            SetCallState(SipCallState.Ending, detail: "Transfer complete");
            await CloseConsultationCoreAsync(false, true);
            try { if (_userAgent.IsCallActive) _userAgent.Hangup(); } catch { }
            await CloseMediaAsync();
            _primaryCallId = "";
            _isMuted = false;
            SetCallState(SipCallState.Idle, detail: "Ready");
            return new(true, "Transfer completed");
        }
        catch (Exception ex)
        {
            var primaryConnected = _userAgent?.IsCallActive == true;
            var consultationConnected = _consultationAgent?.IsCallActive == true;
            if (primaryConnected && consultationConnected)
            {
                _transferState.TryTransition(
                    SipTransferState.ConsultationConnected,
                    detail: "Transfer could not be completed");
                return new(false, "The transfer could not be completed. The consultation remains connected so you can retry or cancel.");
            }

            _transferState.TryTransition(SipTransferState.Failed, detail: "Transfer failed");
            await CloseConsultationCoreAsync(primaryConnected, false);
            return new(false, SafeText(ex.Message, "The transfer could not be completed."));
        }
        finally
        {
            _transferGate.Release();
        }
    }

    public async Task<SipOperationResult> CancelAttendedTransferAsync()
    {
        if (!_transferState.Current.CanCancel) return new(false, "There is no consultation to cancel.");
        try
        {
            if (_consultationAgent?.IsCalling == true || _consultationAgent?.IsRinging == true)
                _consultationAgent.Cancel();
        }
        catch { }
        await StopConsultationAsync(true, true);
        return new(true, "Consultation cancelled. The original call has resumed.");
    }

    private async Task StopConsultationAsync(bool resumePrimary, bool resetState)
    {
        try
        {
            if (_consultationAgent?.IsCalling == true || _consultationAgent?.IsRinging == true)
                _consultationAgent.Cancel();
        }
        catch { }
        await _transferGate.WaitAsync();
        try
        {
            await CloseConsultationCoreAsync(resumePrimary, resetState);
        }
        finally
        {
            _transferGate.Release();
        }
    }

    private async Task HandleConsultationEndedAsync(string detail)
    {
        await _transferGate.WaitAsync();
        try
        {
            if (_consultationEnding || !_transferState.Current.IsActive) return;
            _transferState.TryTransition(SipTransferState.Failed, detail: detail);
            await CloseConsultationCoreAsync(true, false);
        }
        finally
        {
            _transferGate.Release();
        }
    }

    private async Task CloseConsultationCoreAsync(bool resumePrimary, bool resetState)
    {
        _consultationEnding = true;
        var agent = _consultationAgent;
        var audio = _consultationAudio;
        var media = _consultationMedia;
        _consultationAgent = null;
        _consultationAudio = null;
        _consultationMedia = null;
        _consultationQualityMonitor = null;
        try
        {
            if (agent is not null)
            {
                try
                {
                    if (agent.IsCalling || agent.IsRinging) agent.Cancel();
                    else if (agent.IsCallActive) agent.Hangup();
                }
                catch { }
            }
            if (audio is not null)
            {
                try { await audio.CloseAudio(); } catch { }
                try { await audio.CloseAudioSink(); } catch { }
            }
            try { media?.Close("Consultation ended"); } catch { }
            try { agent?.Close(); } catch { }

            if (resumePrimary && _userAgent?.IsCallActive == true)
            {
                try { await ResumeLegAsync(_userAgent, _audio); } catch { }
                SetCallState(SipCallState.Connected, detail: "Connected");
                CallQualityChanged?.Invoke(_qualityMonitor?.Current ?? SipCallQualitySnapshot.Empty);
            }
            if (resetState && _transferState.Current.State != SipTransferState.None)
                _transferState.TryTransition(SipTransferState.None, detail: "Ready");
        }
        finally
        {
            _consultationEnding = false;
        }
    }

    private async Task CloseMediaAsync()
    {
        if (_audio is not null)
        {
            try { await _audio.CloseAudio(); } catch { }
            try { await _audio.CloseAudioSink(); } catch { }
        }
        try { _media?.Close("Call ended"); } catch { }
        _media = null; _audio = null; _qualityMonitor = null;
        CallQualityChanged?.Invoke(SipCallQualitySnapshot.Empty);
    }

    private SIPUserAgent? ActiveUserAgent => _activeCallIsWaiting ? _waitingAgent : _userAgent;
    private WindowsAudioEndPoint? ActiveAudio => _activeCallIsWaiting ? _waitingAudio : _audio;

    private SIPUserAgent CreateCallAgent(SIPTransport transport)
    {
        var agent = new SIPUserAgent(transport, null, false);
        agent.OnCallHungup += dialogue => _ = HandleCallAgentEndedAsync(agent, "Call ended");
        agent.ClientCallTrying += (_, _) =>
        {
            if (ReferenceEquals(agent, _userAgent) && !_activeCallIsWaiting)
                SetCallState(SipCallState.OutboundDialing, detail: "Calling");
        };
        agent.ClientCallRinging += (_, response) =>
        {
            if (ReferenceEquals(agent, _userAgent) && !_activeCallIsWaiting)
                SetCallState(
                    SipCallState.OutboundRinging,
                    detail: SipCallProgress.DescribeProvisionalResponse((int)response.Status, !string.IsNullOrWhiteSpace(response.Body)));
        };
        agent.ClientCallAnswered += (_, _) =>
        {
            if (ReferenceEquals(agent, _userAgent) && !_activeCallIsWaiting)
                SetCallState(SipCallState.Connected, detail: "Connected");
        };
        agent.ClientCallFailed += (_, error, _) =>
        {
            if (ReferenceEquals(agent, _userAgent) && !_activeCallIsWaiting)
                _ = CompleteFailedCallAsync(SafeText(error, "Call failed"));
        };
        agent.ServerCallCancelled += (serverAgent, cancelRequest) => _ = HandlePendingCallEndedAsync(agent, "Incoming call cancelled");
        agent.ServerCallRingTimeout += serverAgent => _ = HandlePendingCallEndedAsync(agent, "Incoming call timed out");
        agent.RemotePutOnHold += () => ApplyRemoteHold(agent, true);
        agent.RemoteTookOffHold += () => ApplyRemoteHold(agent, false);
        return agent;
    }

    private async Task HandleTransportRequestAsync(SIPEndPoint localEndPoint, SIPEndPoint remoteEndPoint, SIPRequest request)
    {
        if (await TryHandleUnsolicitedNotifyAsync(request)) return;
        if (!SipIncomingCallPolicy.IsInitialInvite(request.Method, request.Header.To?.ToTag, request.Header.Replaces))
            return;

        await _callWaitingGate.WaitAsync();
        try
        {
            HandleIncomingCall(request);
        }
        finally
        {
            _callWaitingGate.Release();
        }
    }

    private void HandleIncomingCall(SIPRequest request)
    {
        if (!IsRegistered || _transport is null || _userAgent is null)
        {
            RejectInitialInvite(request, SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Phone is offline");
            return;
        }

        SIPServerUserAgent? pending = null;
        SIPUserAgent? unacceptedWaitingAgent = null;
        var isWaitingCall = false;
        var (remoteParty, remoteNumber) = GetRemoteParty(request);
        try
        {
            lock (_sync)
            {
                var state = _callState.Current;
                var callId = request.Header.CallId;
                if (string.Equals(_pendingIncomingCall?.CallRequest?.Header.CallId, callId, StringComparison.Ordinal)
                    || string.Equals(_waitingIncomingCall?.CallRequest?.Header.CallId, callId, StringComparison.Ordinal))
                {
                    return;
                }

                var disposition = SipIncomingCallPolicy.Classify(
                    IsRegistered,
                    state,
                    _pendingIncomingCall is not null,
                    _waitingState.Current,
                    _waitingAgent is not null,
                    _transferState.Current.IsActive);
                if (disposition == SipIncomingCallDisposition.Primary)
                {
                    pending = _userAgent.AcceptCall(request);
                    _pendingIncomingCall = pending;
                    _primaryDirection = SipCallDirection.Inbound;
                    _primaryRemoteParty = remoteParty;
                    _primaryRemoteNumber = remoteNumber;
                    _primaryCallId = Guid.NewGuid().ToString("N");
                }
                else if (disposition == SipIncomingCallDisposition.Waiting)
                {
                    if (_waitingState.Current.State == SipCallWaitingState.Failed)
                        _waitingState.TryTransition(SipCallWaitingState.None, detail: "Ready");
                    var waitingAgent = CreateCallAgent(_transport);
                    try
                    {
                        pending = waitingAgent.AcceptCall(request);
                    }
                    catch
                    {
                        unacceptedWaitingAgent = waitingAgent;
                        throw;
                    }
                    _waitingAgent = waitingAgent;
                    _waitingIncomingCall = pending;
                    _waitingRemoteParty = remoteParty;
                    _waitingRemoteNumber = remoteNumber;
                    _waitingCallId = Guid.NewGuid().ToString("N");
                    isWaitingCall = true;
                }
            }
        }
        catch
        {
            pending = null;
        }
        try { unacceptedWaitingAgent?.Close(); } catch { }

        if (pending is null)
        {
            RejectInitialInvite(request, SIPResponseStatusCodesEnum.BusyHere, "Phone is busy");
            return;
        }

        if (isWaitingCall)
        {
            if (!_waitingState.TryTransition(
                SipCallWaitingState.Ringing,
                remoteParty,
                remoteNumber,
                _callState.Current.RemoteParty,
                detail: "Call waiting"))
            {
                var waitingAgent = _waitingAgent;
                _waitingAgent = null;
                _waitingIncomingCall = null;
                _waitingRemoteParty = "";
                _waitingRemoteNumber = "";
                _waitingCallId = "";
                try { pending.Reject(SIPResponseStatusCodesEnum.BusyHere, "Phone is busy"); } catch { }
                try { waitingAgent?.Close(); } catch { }
                return;
            }
            PublishLifecycle(_waitingCallId, SipCallDirection.Inbound, _waitingRemoteNumber, SipCallLifecycleState.Ringing);
        }
        else if (!SetCallState(SipCallState.IncomingRinging, SipCallDirection.Inbound, remoteParty, "Incoming call", remoteNumber))
        {
            lock (_sync) _pendingIncomingCall = null;
            _primaryCallId = "";
            try { pending.Reject(SIPResponseStatusCodesEnum.BusyHere, "Phone is busy"); } catch { }
        }
        else
        {
            PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Ringing);
        }
    }

    private void RejectInitialInvite(SIPRequest request, SIPResponseStatusCodesEnum status, string reason)
    {
        if (_transport is null) return;
        SIPUserAgent? rejectAgent = null;
        try
        {
            rejectAgent = new SIPUserAgent(_transport, null, false);
            rejectAgent.AcceptCall(request).Reject(status, reason);
        }
        catch { }
        finally { try { rejectAgent?.Close(); } catch { } }
    }

    private async Task<SipOperationResult> AnswerWaitingCallAsync()
    {
        await _callWaitingGate.WaitAsync();
        try
        {
            var pending = _waitingIncomingCall;
            var waitingAgent = _waitingAgent;
            if (!_waitingState.Current.CanAnswer || pending is null || waitingAgent is null || _userAgent?.IsCallActive != true)
                return new(false, "The waiting call is no longer available.");

            _waitingState.TryTransition(SipCallWaitingState.Answering, detail: "Answering waiting call");
            await HoldLegAsync(_userAgent, _audio);
            PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Hold);
            SetCallState(
                SipCallState.Held,
                _primaryDirection,
                _primaryRemoteParty,
                "First call on hold",
                _primaryRemoteNumber);

            _waitingAudio = CreateAudioEndpoint();
            _waitingMedia = CreateMediaSession(_waitingAudio, SipMediaSlot.Waiting);
            _waitingIncomingCall = null;
            var answered = await waitingAgent.Answer(pending, _waitingMedia);
            if (!answered)
            {
                await CloseWaitingResourcesAsync(false, "Waiting call was not answered");
                await ResumePrimaryAfterWaitingAsync();
                _waitingState.TryTransition(SipCallWaitingState.Failed, detail: "Waiting call ended before it could be answered");
                return new(false, "The waiting call ended before it could be answered. The first call has resumed.");
            }

            if (_isMuted && _waitingAudio is not null)
                await _waitingAudio.PauseAudio();
            PublishLifecycle(_waitingCallId, SipCallDirection.Inbound, _waitingRemoteNumber, SipCallLifecycleState.Answered);
            _activeCallIsWaiting = true;
            SetCallState(
                SipCallState.Connected,
                SipCallDirection.Inbound,
                _waitingRemoteParty,
                "Connected - two calls",
                _waitingRemoteNumber);
            _waitingState.TryTransition(
                SipCallWaitingState.TwoCalls,
                activeParty: _waitingRemoteParty,
                heldParty: _primaryRemoteParty,
                detail: "Two calls connected");
            CallQualityChanged?.Invoke(_waitingQualityMonitor?.Current ?? SipCallQualitySnapshot.Empty);
            return new(true, "Waiting call answered. The first call is on hold.");
        }
        catch (Exception ex)
        {
            await CloseWaitingResourcesAsync(false, "Waiting call failed");
            await ResumePrimaryAfterWaitingAsync();
            _waitingState.TryTransition(SipCallWaitingState.Failed, detail: "Waiting call could not be answered");
            return new(false, SafeText(ex.Message, "The waiting call could not be answered."));
        }
        finally
        {
            _callWaitingGate.Release();
        }
    }

    private async Task RejectWaitingCallAsync()
    {
        await _callWaitingGate.WaitAsync();
        try
        {
            var pending = _waitingIncomingCall;
            if (pending is null) return;
            _waitingIncomingCall = null;
            PublishLifecycle(_waitingCallId, SipCallDirection.Inbound, _waitingRemoteNumber, SipCallLifecycleState.Declined);
            try { pending.Reject(SIPResponseStatusCodesEnum.Decline, "Call declined"); } catch { }
            await CloseWaitingResourcesAsync(false, "Waiting call declined");
            _waitingState.TryTransition(SipCallWaitingState.None, detail: "Ready");
        }
        finally
        {
            _callWaitingGate.Release();
        }
    }

    public async Task<SipOperationResult> SwapCallsAsync()
    {
        await _callWaitingGate.WaitAsync();
        try
        {
            if (!_waitingState.Current.CanSwap || _userAgent?.IsCallActive != true || _waitingAgent?.IsCallActive != true)
                return new(false, "Two connected calls are required before switching.");

            if (_activeCallIsWaiting)
            {
                await HoldLegAsync(_waitingAgent, _waitingAudio);
                try { await ResumeLegAsync(_userAgent, _audio); }
                catch
                {
                    try { await ResumeLegAsync(_waitingAgent, _waitingAudio); } catch { }
                    throw;
                }
                PublishLifecycle(_waitingCallId, SipCallDirection.Inbound, _waitingRemoteNumber, SipCallLifecycleState.Hold);
                PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Resume);
                _activeCallIsWaiting = false;
                SetCallState(SipCallState.Connected, _primaryDirection, _primaryRemoteParty, "Connected - two calls", _primaryRemoteNumber);
            }
            else
            {
                await HoldLegAsync(_userAgent, _audio);
                try { await ResumeLegAsync(_waitingAgent, _waitingAudio); }
                catch
                {
                    try { await ResumeLegAsync(_userAgent, _audio); } catch { }
                    throw;
                }
                PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Hold);
                PublishLifecycle(_waitingCallId, SipCallDirection.Inbound, _waitingRemoteNumber, SipCallLifecycleState.Resume);
                _activeCallIsWaiting = true;
                SetCallState(SipCallState.Connected, SipCallDirection.Inbound, _waitingRemoteParty, "Connected - two calls", _waitingRemoteNumber);
            }

            _waitingState.TryTransition(
                SipCallWaitingState.TwoCalls,
                activeParty: _activeCallIsWaiting ? _waitingRemoteParty : _primaryRemoteParty,
                heldParty: _activeCallIsWaiting ? _primaryRemoteParty : _waitingRemoteParty,
                detail: "Active call switched");
            CallQualityChanged?.Invoke(CurrentQuality);
            return new(true, "Calls switched");
        }
        catch (Exception ex)
        {
            return new(false, SafeText(ex.Message, "The calls could not be switched."));
        }
        finally
        {
            _callWaitingGate.Release();
        }
    }

    private static async Task HoldLegAsync(SIPUserAgent agent, WindowsAudioEndPoint? audio)
    {
        if (!agent.IsOnLocalHold) agent.PutOnHold();
        if (audio is not null)
        {
            await audio.PauseAudio();
            await audio.PauseAudioSink();
        }
    }

    private async Task ResumeLegAsync(SIPUserAgent agent, WindowsAudioEndPoint? audio)
    {
        if (agent.IsOnLocalHold) agent.TakeOffHold();
        if (audio is not null)
        {
            await audio.ResumeAudio();
            await audio.ResumeAudioSink();
            if (_isMuted) await audio.PauseAudio();
        }
    }

    private async Task ResumePrimaryAfterWaitingAsync()
    {
        _activeCallIsWaiting = false;
        if (_userAgent?.IsCallActive == true)
        {
            await ResumeLegAsync(_userAgent, _audio);
            PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Resume);
            SetCallState(SipCallState.Connected, _primaryDirection, _primaryRemoteParty, "Connected", _primaryRemoteNumber);
            CallQualityChanged?.Invoke(_qualityMonitor?.Current ?? SipCallQualitySnapshot.Empty);
        }
    }

    private async Task CloseWaitingResourcesAsync(bool signalCall, string reason)
    {
        var agent = _waitingAgent;
        var audio = _waitingAudio;
        var media = _waitingMedia;
        _waitingAgent = null;
        _waitingIncomingCall = null;
        _waitingAudio = null;
        _waitingMedia = null;
        _waitingQualityMonitor = null;
        _activeCallIsWaiting = false;
        _waitingRemoteParty = "";
        _waitingRemoteNumber = "";
        _waitingCallId = "";

        if (signalCall && agent is not null)
        {
            try
            {
                if (agent.IsCalling || agent.IsRinging) agent.Cancel();
                else if (agent.IsCallActive) agent.Hangup();
            }
            catch { }
        }
        if (audio is not null)
        {
            try { await audio.CloseAudio(); } catch { }
            try { await audio.CloseAudioSink(); } catch { }
        }
        try { media?.Close(reason); } catch { }
        try { agent?.Close(); } catch { }
    }

    private void ApplyRemoteHold(SIPUserAgent agent, bool held)
    {
        if (!ReferenceEquals(agent, ActiveUserAgent)) return;
        if (_activeCallIsWaiting)
        {
            SetCallState(
                held ? SipCallState.Held : SipCallState.Connected,
                SipCallDirection.Inbound,
                _waitingRemoteParty,
                held ? "Held by the other party" : "Connected",
                _waitingRemoteNumber);
            PublishLifecycle(
                _waitingCallId,
                SipCallDirection.Inbound,
                _waitingRemoteNumber,
                held ? SipCallLifecycleState.Hold : SipCallLifecycleState.Resume);
        }
        else
        {
            SetCallState(
                held ? SipCallState.Held : SipCallState.Connected,
                _primaryDirection,
                _primaryRemoteParty,
                held ? "Held by the other party" : "Connected",
                _primaryRemoteNumber);
            PublishLifecycle(
                _primaryCallId,
                _primaryDirection,
                _primaryRemoteNumber,
                held ? SipCallLifecycleState.Hold : SipCallLifecycleState.Resume);
        }
    }

    private async Task HandlePendingCallEndedAsync(SIPUserAgent agent, string detail)
    {
        if (ReferenceEquals(agent, _waitingAgent))
        {
            await _callWaitingGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(agent, _waitingAgent) || _waitingIncomingCall is null) return;
                PublishLifecycle(_waitingCallId, SipCallDirection.Inbound, _waitingRemoteNumber, SipCallLifecycleState.Missed);
                await CloseWaitingResourcesAsync(false, detail);
                if (_waitingState.Current.State is not SipCallWaitingState.None)
                    _waitingState.TryTransition(SipCallWaitingState.None, detail: "Ready");
            }
            finally
            {
                _callWaitingGate.Release();
            }
            return;
        }

        if (!ReferenceEquals(agent, _userAgent)) return;
        var hadPendingCall = false;
        lock (_sync)
        {
            if (_pendingIncomingCall is not null)
            {
                _pendingIncomingCall = null;
                hadPendingCall = true;
            }
        }
        if (!hadPendingCall) return;
        PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Missed);
        SetCallState(SipCallState.Ending, detail: detail);
        await CloseMediaAsync();
        _primaryCallId = "";
        _isMuted = false;
        SetCallState(SipCallState.Idle, detail: "Ready");
    }

    private async Task HandleCallAgentEndedAsync(SIPUserAgent agent, string detail)
    {
        if (ReferenceEquals(agent, _waitingAgent))
        {
            await _callWaitingGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(agent, _waitingAgent)) return;
                var wasActive = _activeCallIsWaiting;
                PublishLifecycle(_waitingCallId, SipCallDirection.Inbound, _waitingRemoteNumber, SipCallLifecycleState.Ended);
                await CloseWaitingResourcesAsync(false, detail);
                if (wasActive) await ResumePrimaryAfterWaitingAsync();
                if (_waitingState.Current.State is not SipCallWaitingState.None)
                    _waitingState.TryTransition(SipCallWaitingState.None, detail: "Ready");
            }
            finally
            {
                _callWaitingGate.Release();
            }
            return;
        }

        if (!ReferenceEquals(agent, _userAgent)) return;
        await _callWaitingGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(agent, _userAgent)) return;
            PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Ended);
            if (_waitingState.Current.HasTwoCalls)
            {
                await PromoteWaitingToPrimaryAsync(!_activeCallIsWaiting, detail);
                return;
            }
            if (_waitingState.Current.State == SipCallWaitingState.Ringing && _waitingIncomingCall is not null)
            {
                await PromoteWaitingIncomingToPrimaryAsync(detail);
                return;
            }
        }
        finally
        {
            _callWaitingGate.Release();
        }

        await CompleteRemoteHangupAsync(detail);
    }

    private async Task EndActiveCallWithWaitingAsync()
    {
        await _callWaitingGate.WaitAsync();
        try
        {
            if (!_waitingState.Current.HasTwoCalls) return;
            if (_activeCallIsWaiting)
            {
                PublishLifecycle(_waitingCallId, SipCallDirection.Inbound, _waitingRemoteNumber, SipCallLifecycleState.Hangup);
                await CloseWaitingResourcesAsync(true, "Call ended");
                await ResumePrimaryAfterWaitingAsync();
                _waitingState.TryTransition(SipCallWaitingState.None, detail: "Ready");
            }
            else
            {
                PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Hangup);
                try { if (_userAgent?.IsCallActive == true) _userAgent.Hangup(); } catch { }
                await PromoteWaitingToPrimaryAsync(true, "Call ended");
            }
        }
        finally
        {
            _callWaitingGate.Release();
        }
    }

    private async Task PromoteWaitingToPrimaryAsync(bool resume, string detail)
    {
        var survivingAgent = _waitingAgent;
        if (survivingAgent?.IsCallActive != true)
        {
            await CloseWaitingResourcesAsync(false, detail);
            _waitingState.TryTransition(SipCallWaitingState.None, detail: "Ready");
            SetCallState(SipCallState.Ending, detail: detail);
            SetCallState(SipCallState.Idle, detail: "Ready");
            return;
        }

        var oldPrimaryAgent = _userAgent;
        await CloseMediaAsync();
        _userAgent = survivingAgent;
        _audio = _waitingAudio;
        _media = _waitingMedia;
        _qualityMonitor = _waitingQualityMonitor;
        _primaryDirection = SipCallDirection.Inbound;
        _primaryRemoteParty = _waitingRemoteParty;
        _primaryRemoteNumber = _waitingRemoteNumber;
        _primaryCallId = _waitingCallId;

        _waitingAgent = null;
        _waitingIncomingCall = null;
        _waitingAudio = null;
        _waitingMedia = null;
        _waitingQualityMonitor = null;
        _waitingRemoteParty = "";
        _waitingRemoteNumber = "";
        _waitingCallId = "";
        _activeCallIsWaiting = false;
        try { oldPrimaryAgent?.Close(); } catch { }

        if (resume)
        {
            await ResumeLegAsync(_userAgent, _audio);
            PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Resume);
        }
        _waitingState.TryTransition(SipCallWaitingState.None, detail: "Ready");
        SetCallState(SipCallState.Connected, _primaryDirection, _primaryRemoteParty, "Connected", _primaryRemoteNumber);
        CallQualityChanged?.Invoke(_qualityMonitor?.Current ?? SipCallQualitySnapshot.Empty);
    }

    private async Task PromoteWaitingIncomingToPrimaryAsync(string detail)
    {
        var survivingAgent = _waitingAgent;
        var survivingPending = _waitingIncomingCall;
        if (survivingAgent is null || survivingPending is null)
        {
            await CompleteRemoteHangupAsync(detail);
            return;
        }

        var oldPrimaryAgent = _userAgent;
        await CloseMediaAsync();
        _userAgent = survivingAgent;
        _pendingIncomingCall = survivingPending;
        _primaryDirection = SipCallDirection.Inbound;
        _primaryRemoteParty = _waitingRemoteParty;
        _primaryRemoteNumber = _waitingRemoteNumber;
        _primaryCallId = _waitingCallId;
        _waitingAgent = null;
        _waitingIncomingCall = null;
        _waitingRemoteParty = "";
        _waitingRemoteNumber = "";
        _waitingCallId = "";
        _activeCallIsWaiting = false;
        try { oldPrimaryAgent?.Close(); } catch { }

        _waitingState.TryTransition(SipCallWaitingState.None, detail: "Ready");
        SetCallState(SipCallState.Ending, detail: detail);
        SetCallState(SipCallState.Idle, detail: "Ready");
        SetCallState(
            SipCallState.IncomingRinging,
            SipCallDirection.Inbound,
            _primaryRemoteParty,
            "Incoming call",
            _primaryRemoteNumber);
    }

    private async Task CompleteRemoteHangupAsync(string detail = "Call ended")
    {
        PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Ended);
        lock (_sync) _pendingIncomingCall = null;
        await StopConsultationAsync(false, true);
        SetCallState(SipCallState.Ending, detail: detail);
        await CloseMediaAsync();
        _primaryCallId = "";
        _isMuted = false;
        SetCallState(SipCallState.Idle, detail: "Ready");
    }

    private async Task CompleteFailedCallAsync(string detail)
    {
        PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Failed);
        lock (_sync) _pendingIncomingCall = null;
        await StopConsultationAsync(false, true);
        await CloseMediaAsync();
        _primaryCallId = "";
        _isMuted = false;
        SetCallState(SipCallState.Failed, detail: detail);
    }

    private bool SetCallState(
        SipCallState state,
        SipCallDirection direction = SipCallDirection.None,
        string remoteParty = "",
        string detail = "",
        string remoteNumber = "") =>
        _callState.TryTransition(state, direction, remoteParty, SafeText(detail, state.ToString()), remoteNumber);

    private static (string Display, string Number) GetRemoteParty(SIPRequest request)
    {
        var displayName = SafeText(request.Header.From?.FromName, "");
        var user = SafeText(request.Header.From?.FromURI?.User, "Unknown caller");
        return (string.IsNullOrWhiteSpace(displayName) ? user : $"{displayName} ({user})", user);
    }

    private static string SafeText(string? value, string fallback)
    {
        var safe = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safe.Length > 128) safe = safe[..128];
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe;
    }

    private void PublishLifecycle(
        string callId,
        SipCallDirection direction,
        string remoteNumber,
        SipCallLifecycleState state)
    {
        if (string.IsNullOrWhiteSpace(callId) || direction == SipCallDirection.None) return;
        var callEvent = new SipCallLifecycleEvent(
            callId,
            direction,
            SafeText(remoteNumber, ""),
            state,
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"));

        if (callEvent.IsTerminal)
        {
            lock (_sync)
            {
                if (!_terminalCallIds.Add(callId)) return;
                _terminalCallOrder.Enqueue(callId);
                while (_terminalCallOrder.Count > 512)
                    _terminalCallIds.Remove(_terminalCallOrder.Dequeue());
            }
        }

        try { CallLifecycleChanged?.Invoke(callEvent); } catch { }
    }

    private WindowsAudioEndPoint CreateAudioEndpoint()
    {
        string microphoneDeviceId;
        string speakerDeviceId;
        string codecProfileName;
        lock (_sync)
        {
            microphoneDeviceId = _microphoneDeviceId;
            speakerDeviceId = _speakerDeviceId;
            codecProfileName = _codecProfileName;
        }

        var microphone = WindowsAudioDeviceCatalog.ResolveMicrophone(microphoneDeviceId);
        var speaker = WindowsAudioDeviceCatalog.ResolveSpeaker(speakerDeviceId);
        if (microphone.UserMessage is not null) AudioStatusChanged?.Invoke(microphone.UserMessage);
        if (speaker.UserMessage is not null) AudioStatusChanged?.Invoke(speaker.UserMessage);

        var codecProfile = SipCodecProfile.Resolve(codecProfileName);
        var audio = new WindowsAudioEndPoint(
            new AudioEncoder(includeOpus: codecProfile.IncludeOpus),
            speaker.Device.DeviceIndex,
            microphone.Device.DeviceIndex);
        audio.RestrictFormats(codecProfile.Allows);
        audio.OnAudioSourceError += _ => AudioStatusChanged?.Invoke("The microphone stopped working. Reconnect it or choose another microphone in Settings.");
        audio.OnAudioSinkError += _ => AudioStatusChanged?.Invoke("The speaker stopped working. Reconnect it or choose another speaker in Settings.");
        return audio;
    }

    private RTPSession CreateMediaSession(WindowsAudioEndPoint audio, SipMediaSlot slot)
    {
        var session = SipMediaSessionPolicy.CreateAudioSession();
        var qualityMonitor = new SipCallQualityMonitor();
        switch (slot)
        {
            case SipMediaSlot.Primary:
                _qualityMonitor = qualityMonitor;
                break;
            case SipMediaSlot.Consultation:
                _consultationQualityMonitor = qualityMonitor;
                break;
            case SipMediaSlot.Waiting:
                _waitingQualityMonitor = qualityMonitor;
                break;
        }
        qualityMonitor.Changed += snapshot =>
        {
            var activeMonitor = _transferState.Current.IsActive
                ? _consultationQualityMonitor
                : _activeCallIsWaiting ? _waitingQualityMonitor : _qualityMonitor;
            if (ReferenceEquals(activeMonitor, qualityMonitor)) CallQualityChanged?.Invoke(snapshot);
        };
        AudioFormat? negotiatedFormat = null;
        session.addTrack(new MediaStreamTrack(audio.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv));
        audio.OnAudioSourceEncodedSample += session.SendAudio;
        session.OnAudioFormatsNegotiated += formats =>
        {
            var selected = formats.First();
            negotiatedFormat = selected;
            qualityMonitor.ObserveCodec(selected.Codec.ToString(), selected.RtpClockRate);
            audio.SetAudioSourceFormat(selected);
            audio.SetAudioSinkFormat(selected);
        };
        session.OnRtpPacketReceived += (remote, mediaType, packet) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio && negotiatedFormat is { } format)
            {
                qualityMonitor.ObserveReceivedPacket();
                audio.GotEncodedMediaFrame(new EncodedAudioFrame(0, format, 20, packet.Payload));
            }
        };
        session.OnSendReport += (mediaType, report) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio) qualityMonitor.ObserveLocalReport(report);
        };
        session.OnReceiveReport += (remote, mediaType, report) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio) qualityMonitor.ObserveRemoteReport(report);
        };
        session.OnStarted += () => _ = audio.Start();
        return session;
    }

    private enum SipMediaSlot
    {
        Primary,
        Consultation,
        Waiting
    }

    private async Task StopWaitingCallsAsync()
    {
        await _callWaitingGate.WaitAsync();
        try
        {
            var pending = _waitingIncomingCall;
            _waitingIncomingCall = null;
            PublishLifecycle(_waitingCallId, SipCallDirection.Inbound, _waitingRemoteNumber, SipCallLifecycleState.Ended);
            if (pending is not null)
            {
                try { pending.Reject(SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Phone is stopping"); } catch { }
            }
            await CloseWaitingResourcesAsync(true, "Phone stopped");
            if (_waitingState.Current.State is not SipCallWaitingState.None)
                _waitingState.TryTransition(SipCallWaitingState.None, detail: "Ready");
        }
        finally
        {
            _callWaitingGate.Release();
        }
    }

    private async Task ShutdownAsync()
    {
        IsRegistered = false;
        PublishLifecycle(_primaryCallId, _primaryDirection, _primaryRemoteNumber, SipCallLifecycleState.Ended);
        SIPServerUserAgent? pendingPrimary;
        lock (_sync)
        {
            pendingPrimary = _pendingIncomingCall;
            _pendingIncomingCall = null;
        }
        if (pendingPrimary is not null)
        {
            try { pendingPrimary.Reject(SIPResponseStatusCodesEnum.TemporarilyUnavailable, "Phone is stopping"); } catch { }
        }
        await StopWaitingCallsAsync();
        await StopConsultationAsync(false, true);
        await CloseMediaAsync();
        StopFeatureSubscriptions();
        Voicemail = VoicemailStatus.Unknown;
        try { _registration?.Stop(true); } catch { }
        if (_transport is not null) _transport.SIPTransportRequestReceived -= HandleTransportRequestAsync;
        try { _transport?.Shutdown(); } catch { }
        try { _userAgent?.Close(); } catch { }
        _registration = null; _userAgent = null; _transport = null;
        _activeCallIsWaiting = false;
        _primaryDirection = SipCallDirection.None;
        _primaryRemoteParty = "";
        _primaryRemoteNumber = "";
        _primaryCallId = "";
        _isMuted = false;
        _server = "";
        _username = "";
        _password = "";
        var state = _callState.Current.State;
        if (state is not (SipCallState.Idle or SipCallState.Failed))
            SetCallState(SipCallState.Ending, detail: "Phone stopped");
        if (_callState.Current.State is SipCallState.Ending or SipCallState.Failed)
            SetCallState(SipCallState.Idle, detail: "Ready");
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync();
}
