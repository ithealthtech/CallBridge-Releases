using SIPSorcery.SIP;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace CallBridge.Desktop;

public enum SipSignalingTransport
{
    Udp,
    Tcp,
    Tls
}

public static class SipTransportProfile
{
    public static IReadOnlyList<string> DisplayNames { get; } = ["UDP", "TCP", "TLS"];

    public static bool TryParse(string? value, out SipSignalingTransport transport)
    {
        transport = value?.Trim().ToUpperInvariant() switch
        {
            "TCP" => SipSignalingTransport.Tcp,
            "TLS" => SipSignalingTransport.Tls,
            "UDP" => SipSignalingTransport.Udp,
            _ => (SipSignalingTransport)(-1)
        };
        return Enum.IsDefined(transport);
    }

    public static string ToDisplayName(SipSignalingTransport transport) => transport switch
    {
        SipSignalingTransport.Udp => "UDP",
        SipSignalingTransport.Tcp => "TCP",
        SipSignalingTransport.Tls => "TLS",
        _ => throw new ArgumentOutOfRangeException(nameof(transport))
    };

    /// <summary>
    /// A <c>sips:</c> server address or port 5061 always means TLS, whatever transport is stored,
    /// so a secure address can never be silently downgraded to plain TCP or UDP.
    /// </summary>
    public static SipSignalingTransport ResolveTransport(string? server, SipSignalingTransport configured)
    {
        var value = (server ?? "").Trim().TrimEnd('/');
        if (value.StartsWith("sips:", StringComparison.OrdinalIgnoreCase)) return SipSignalingTransport.Tls;
        var hostPart = value.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? value[4..] : value;
        var at = hostPart.LastIndexOf('@');
        if (at >= 0) hostPart = hostPart[(at + 1)..];
        hostPart = hostPart.Split(';', 2)[0];
        var colon = hostPart.LastIndexOf(':');
        if (colon > 0 && !hostPart.EndsWith(']') && int.TryParse(hostPart[(colon + 1)..], out var port) && port == 5061)
            return SipSignalingTransport.Tls;
        return configured;
    }

    public static SIPURI BuildRegistrarUri(string server, SipSignalingTransport transport)
    {
        var parsed = ParseUri(server, "SIP server");
        return CreateUri(null, parsed.Host, transport);
    }

    public static SIPURI BuildDestinationUri(string destination, string server, SipSignalingTransport transport)
    {
        if (string.IsNullOrWhiteSpace(destination)) throw new FormatException("Enter a destination.");
        var serverUri = BuildRegistrarUri(server, transport);
        SIPURI parsed;
        var trimmed = destination.Trim();
        if (trimmed.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("sips:", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains('@'))
        {
            parsed = ParseUri(trimmed, "destination");
        }
        else
        {
            parsed = new SIPURI(trimmed, serverUri.Host, null);
        }

        if (string.IsNullOrWhiteSpace(parsed.User)) throw new FormatException("The destination does not contain a phone number or extension.");
        return CreateUri(parsed.User, parsed.Host, transport);
    }

    public static SIPChannel CreateChannel(SipSignalingTransport transport)
    {
        var endpoint = new IPEndPoint(IPAddress.Any, 0);
        return transport switch
        {
            SipSignalingTransport.Udp => new SIPUDPChannel(endpoint, false),
            SipSignalingTransport.Tcp => new SIPTCPChannel(endpoint, false),
            SipSignalingTransport.Tls => new SIPTLSChannel(endpoint, false, ValidateRemoteCertificate),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
    }

    public static bool AcceptsCertificateErrors(SslPolicyErrors sslPolicyErrors) =>
        sslPolicyErrors == SslPolicyErrors.None;

    private static SIPURI ParseUri(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException($"{label} is required.");
        var candidate = value.Trim().TrimEnd('/');
        if (!candidate.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
        {
            candidate = $"sip:{candidate}";
        }

        try
        {
            var parsed = SIPURI.ParseSIPURI(candidate);
            if (string.IsNullOrWhiteSpace(parsed.Host)) throw new FormatException();
            return parsed;
        }
        catch (Exception ex)
        {
            throw new FormatException($"{label} is not a valid SIP address.", ex);
        }
    }

    private static SIPURI CreateUri(string? user, string host, SipSignalingTransport transport)
    {
        var (scheme, protocol) = transport switch
        {
            SipSignalingTransport.Udp => (SIPSchemesEnum.sip, SIPProtocolsEnum.udp),
            SipSignalingTransport.Tcp => (SIPSchemesEnum.sip, SIPProtocolsEnum.tcp),
            SipSignalingTransport.Tls => (SIPSchemesEnum.sips, SIPProtocolsEnum.tls),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
        return new SIPURI(user, host, null, scheme, protocol);
    }

    private static bool ValidateRemoteCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors) => AcceptsCertificateErrors(sslPolicyErrors);
}
