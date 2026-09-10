using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Windows;

namespace CallBridge.Desktop;

public sealed record SipOperationResult(bool Success, string Message);

/// <summary>
/// SIP signalling transport. TLS is the default; the others exist only for PBXs that
/// cannot offer it, and both send credentials and signalling in the clear.
/// </summary>
public enum SipTransportMode
{
    Tls,
    Tcp,
    Udp
}

public sealed class SipSoftphone : IAsyncDisposable
{
    private const int DefaultTlsPort = 5061;
    private const int DefaultPlaintextPort = 5060;

    private SIPTransport? _transport;
    private SIPRegistrationUserAgent? _registration;
    private SIPUserAgent? _userAgent;
    private WindowsAudioEndPoint? _audio;
    private RTPSession? _media;
    private string _server = "";
    private string _username = "";
    private string _password = "";
    private string _scheme = "sips";

    /// <summary>Transport negotiated for the current registration.</summary>
    public SipTransportMode Transport { get; private set; } = SipTransportMode.Tls;

    /// <summary>True when SIP signalling for the current registration is TLS-protected.</summary>
    public bool IsSignallingEncrypted => Transport == SipTransportMode.Tls;

    public bool IsRegistered { get; private set; }
    public bool IsCallActive => _userAgent?.IsCallActive == true;
    public event Action<string>? RegistrationStateChanged;
    public event Action<string>? CallStateChanged;

    public async Task<SipOperationResult> RegisterAsync(string server, string username, string password, string transport = "TLS")
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return new(false, "SIP server, username, and password are required.");

        await ShutdownAsync();

        var (host, port, mode) = ParseServer(server, ParseTransport(transport));
        Transport = mode;
        _server = host;
        _scheme = mode == SipTransportMode.Tls ? "sips" : "sip";
        _username = username.Trim();
        _password = password;

        _transport = new SIPTransport();
        try
        {
            _transport.AddSIPChannel(CreateChannel(mode));
        }
        catch (Exception ex)
        {
            await ShutdownAsync();
            return new(false, $"Could not open a {Describe(mode)} SIP channel: {ex.Message}");
        }

        var registrar = BuildRegistrarUri(host, port, mode);
        _userAgent = new SIPUserAgent(_transport, null, true);
        _userAgent.OnCallHungup += dialogue => { CallStateChanged?.Invoke("ended"); _ = CloseMediaAsync(); };
        _userAgent.ClientCallTrying += (_, _) => CallStateChanged?.Invoke("trying");
        _userAgent.ClientCallRinging += (_, _) => CallStateChanged?.Invoke("ringing");
        _userAgent.ClientCallAnswered += (_, _) => CallStateChanged?.Invoke("connected");
        _userAgent.ClientCallFailed += (_, error, _) => CallStateChanged?.Invoke($"failed: {error}");

        var completion = new TaskCompletionSource<SipOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registration = new SIPRegistrationUserAgent(_transport, _username, _password, registrar, 300);
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
        catch (TimeoutException)
        {
            var hint = Transport == SipTransportMode.Tls
                ? " The account is set to TLS: confirm the PBX accepts TLS on port 5061 and that its certificate is valid for the configured server name and trusted by this machine."
                : " Signalling is not encrypted on this transport; prefer TLS where the PBX supports it.";
            return new(false, $"SIP registration timed out. Verify the server, account, firewall, and transport requirements.{hint}");
        }
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
            var target = BuildTarget(destination);
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
        var target = BuildTarget(destination);
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

    internal static SipTransportMode ParseTransport(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "UDP" => SipTransportMode.Udp,
        "TCP" => SipTransportMode.Tcp,
        _ => SipTransportMode.Tls
    };

    private static string Describe(SipTransportMode mode) => mode switch
    {
        SipTransportMode.Udp => "UDP",
        SipTransportMode.Tcp => "TCP",
        _ => "TLS"
    };

    /// <summary>
    /// Splits a configured server into host, optional port, and transport. An explicit
    /// <c>sips:</c> scheme or port 5061 always wins over the configured transport, so a
    /// secure address can never be silently downgraded.
    /// </summary>
    internal static (string Host, int? Port, SipTransportMode Mode) ParseServer(string value, SipTransportMode configured)
    {
        var server = value.Trim().TrimEnd('/');
        var mode = configured;

        if (server.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            server = server[5..];
            mode = SipTransportMode.Tls;
        }
        else if (server.StartsWith("sip:", StringComparison.OrdinalIgnoreCase))
        {
            server = server[4..];
        }

        // Strip any user part: a registrar address is a host, not an AOR.
        var at = server.LastIndexOf('@');
        if (at >= 0) server = server[(at + 1)..];

        int? port = null;
        var colon = server.LastIndexOf(':');
        if (colon > 0 && server.IndexOf(']') < colon && int.TryParse(server[(colon + 1)..], out var parsed) && parsed is > 0 and <= 65535)
        {
            port = parsed;
            server = server[..colon];
        }

        if (port == DefaultTlsPort) mode = SipTransportMode.Tls;

        return (server.Trim(), port, mode);
    }

    internal static string BuildRegistrarUri(string host, int? port, SipTransportMode mode) => mode switch
    {
        SipTransportMode.Tls => $"sips:{host}:{port ?? DefaultTlsPort}",
        SipTransportMode.Tcp => $"sip:{host}:{port ?? DefaultPlaintextPort};transport=tcp",
        _ => $"sip:{host}:{port ?? DefaultPlaintextPort}"
    };

    private string BuildTarget(string destination)
    {
        var target = destination.Trim();
        if (target.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            return target;
        if (!target.Contains('@')) target = $"{target}@{_server}";
        return $"{_scheme}:{target}";
    }

    internal static SIPChannel CreateChannel(SipTransportMode mode) => mode switch
    {
        SipTransportMode.Tls => new SIPTLSChannel(new IPEndPoint(IPAddress.Any, 0), true, ValidateServerCertificate),
        SipTransportMode.Tcp => new SIPTCPChannel(new IPEndPoint(IPAddress.Any, 0), true),
        _ => new SIPUDPChannel(new IPEndPoint(IPAddress.Any, 0), true)
    };

    /// <summary>
    /// Validates the PBX certificate against the Windows trust chain. Any policy error -
    /// untrusted root, expired certificate, or a name that does not match the configured
    /// server - fails the connection. There is deliberately no bypass, not even for
    /// debug builds; a PBX with a self-signed certificate must have that certificate
    /// installed in the machine trust store.
    /// </summary>
    private static bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
        => sslPolicyErrors == SslPolicyErrors.None;

    public async ValueTask DisposeAsync() => await ShutdownAsync();
}
