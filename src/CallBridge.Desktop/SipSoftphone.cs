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
    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _registration;
    private SIPUserAgent? _userAgent;
    private WindowsAudioEndPoint? _audio;
    private RTPSession? _media;
    private string _server = "";
    private string _username = "";
    private string _password = "";

    public bool IsRegistered { get; private set; }
    public bool IsCallActive => _userAgent?.IsCallActive == true;
    public event Action<string>? RegistrationStateChanged;
    public event Action<string>? CallStateChanged;

    public async Task<SipOperationResult> RegisterAsync(string server, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new(false, "SIP server, username, and password are required.");

        await ShutdownAsync();
        _server = NormalizeServer(server);
        _username = username.Trim();
        _password = password;
        _transport = new SIPTransport();
        _userAgent = new SIPUserAgent(_transport, null, true);
        _userAgent.OnCallHungup += dialogue => { CallStateChanged?.Invoke("ended"); _ = CloseMediaAsync(); };
        _userAgent.ClientCallTrying += (_, _) => CallStateChanged?.Invoke("trying");
        _userAgent.ClientCallRinging += (_, _) => CallStateChanged?.Invoke("ringing");
        _userAgent.ClientCallAnswered += (_, _) => CallStateChanged?.Invoke("connected");
        _userAgent.ClientCallFailed += (_, error, _) => CallStateChanged?.Invoke($"failed: {error}");

        var completion = new TaskCompletionSource<SipOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registration = new SIPRegistrationUserAgent(_transport, _username, _password, _server, 300);
        _registration.RegistrationSuccessful += (_, _) =>
        {
            IsRegistered = true;
            RegistrationStateChanged?.Invoke("registered");
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
        if (IsCallActive) return new(false, "A call is already active.");
        try
        {
            _audio = new WindowsAudioEndPoint(new AudioEncoder());
            _media = CreateMediaSession(_audio);
            var target = destination.Contains('@') ? destination : $"{destination}@{_server}";
            if (!target.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) && !target.StartsWith("sips:", StringComparison.OrdinalIgnoreCase)) target = $"sip:{target}";
            CallStateChanged?.Invoke("dialing");
            var answered = await _userAgent.Call(target, _username, _password, _media, 30);
            return answered ? new(true, "Connected") : new(false, "The SIP server did not complete the call.");
        }
        catch (Exception ex)
        {
            await CloseMediaAsync();
            return new(false, $"SIP call failed: {ex.Message}");
        }
    }

    public async Task HangupAsync()
    {
        if (_userAgent?.IsCallActive == true || _userAgent?.IsCalling == true) _userAgent.Hangup();
        await CloseMediaAsync();
    }

    public Task SetHoldAsync(bool hold)
    {
        if (_userAgent?.IsCallActive != true) throw new InvalidOperationException("No active SIP call.");
        if (hold) _userAgent.PutOnHold(); else _userAgent.TakeOffHold();
        return Task.CompletedTask;
    }

    public async Task SetMutedAsync(bool muted)
    {
        if (_audio is null) throw new InvalidOperationException("No active SIP audio session.");
        if (muted) await _audio.PauseAudio(); else await _audio.ResumeAudio();
    }

    public async Task SendDtmfAsync(string digits)
    {
        if (_userAgent?.IsCallActive != true) throw new InvalidOperationException("No active SIP call.");
        foreach (var digit in digits)
        {
            var tone = digit switch { >= '0' and <= '9' => (byte)(digit - '0'), '*' => (byte)10, '#' => (byte)11, _ => throw new ArgumentException("DTMF supports 0-9, *, and #.") };
            await _userAgent.SendDtmf(tone);
        }
    }

    public async Task<SipOperationResult> TransferAsync(string destination)
    {
        if (_userAgent?.IsCallActive != true) return new(false, "No active SIP call.");
        var target = destination.Contains('@') ? destination : $"{destination}@{_server}";
        if (!target.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) target = $"sip:{target}";
        var result = await _userAgent.BlindTransfer(SIPURI.ParseSIPURI(target), TimeSpan.FromSeconds(15), CancellationToken.None);
        return result ? new(true, "Transferred") : new(false, "The SIP transfer was rejected.");
    }

    private async Task CloseMediaAsync()
    {
        if (_audio is not null)
        {
            try { await _audio.CloseAudio(); } catch { }
            try { await _audio.CloseAudioSink(); } catch { }
        }
        try { _media?.Close("Call ended"); } catch { }
        _media = null; _audio = null;
    }

    private static RTPSession CreateMediaSession(WindowsAudioEndPoint audio)
    {
        var session = new RTPSession(false, false, false) { AcceptRtpFromAny = true };
        AudioFormat? negotiatedFormat = null;
        session.addTrack(new MediaStreamTrack(audio.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv));
        audio.OnAudioSourceEncodedSample += session.SendAudio;
        session.OnAudioFormatsNegotiated += formats =>
        {
            var selected = formats.First();
            negotiatedFormat = selected;
            audio.SetAudioSourceFormat(selected);
            audio.SetAudioSinkFormat(selected);
        };
        session.OnRtpPacketReceived += (remote, mediaType, packet) =>
        {
            if (mediaType == SDPMediaTypesEnum.audio && negotiatedFormat is { } format)
                audio.GotEncodedMediaFrame(new EncodedAudioFrame(0, format, 20, packet.Payload));
        };
        session.OnStarted += () => _ = audio.Start();
        return session;
    }

    private async Task ShutdownAsync()
    {
        await CloseMediaAsync();
        try { _registration?.Stop(true); } catch { }
        try { _transport?.Shutdown(); } catch { }
        _registration = null; _userAgent = null; _transport = null; IsRegistered = false;
    }

    private static string NormalizeServer(string value)
    {
        var server = value.Trim();
        if (server.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)) server = server[4..];
        if (server.StartsWith("sips:", StringComparison.OrdinalIgnoreCase)) server = server[5..];
        return server.TrimEnd('/');
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync();
}
