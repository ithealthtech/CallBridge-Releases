using System.Net.Security;
using CallBridge.Desktop;
using SIPSorcery.SIP;

var failures = new List<string>();

void Check(string name, bool condition, string detail = "")
{
    if (condition) Console.WriteLine($"  ok   {name}");
    else { Console.WriteLine($"  FAIL {name} {detail}"); failures.Add(name); }
}

Console.WriteLine("Transport selection");

// TLS is the default when nothing usable is configured.
Check("empty transport defaults to TLS", SipSoftphone.ParseTransport("") == SipTransportMode.Tls);
Check("null transport defaults to TLS", SipSoftphone.ParseTransport(null) == SipTransportMode.Tls);
Check("unknown transport defaults to TLS", SipSoftphone.ParseTransport("carrier-pigeon") == SipTransportMode.Tls);
Check("explicit UDP is honoured", SipSoftphone.ParseTransport("udp") == SipTransportMode.Udp);
Check("explicit TCP is honoured", SipSoftphone.ParseTransport("TCP") == SipTransportMode.Tcp);

Console.WriteLine("Server parsing");

var plain = SipSoftphone.ParseServer("pbx.example.invalid", SipTransportMode.Tls);
Check("bare host keeps TLS", plain is { Host: "pbx.example.invalid", Port: null, Mode: SipTransportMode.Tls });

// A secure address must never be silently downgraded by a stale setting.
var scheme = SipSoftphone.ParseServer("sips:pbx.example.invalid", SipTransportMode.Udp);
Check("sips: scheme upgrades UDP to TLS", scheme.Mode == SipTransportMode.Tls, $"got {scheme.Mode}");

var securePort = SipSoftphone.ParseServer("pbx.example.invalid:5061", SipTransportMode.Udp);
Check("port 5061 upgrades UDP to TLS", securePort is { Port: 5061, Mode: SipTransportMode.Tls }, $"got {securePort.Mode}");

var explicitUdp = SipSoftphone.ParseServer("pbx.example.invalid:5060", SipTransportMode.Udp);
Check("explicit UDP on 5060 stays UDP", explicitUdp is { Port: 5060, Mode: SipTransportMode.Udp });

var withUser = SipSoftphone.ParseServer("sips:alice@pbx.example.invalid:5061", SipTransportMode.Tls);
Check("user part is stripped from registrar host", withUser.Host == "pbx.example.invalid", $"got {withUser.Host}");

var sipPrefix = SipSoftphone.ParseServer("sip:pbx.example.invalid", SipTransportMode.Tcp);
Check("sip: prefix is removed", sipPrefix.Host == "pbx.example.invalid");

Console.WriteLine("Registrar URI");

var tlsUri = SipSoftphone.BuildRegistrarUri("pbx.example.invalid", null, SipTransportMode.Tls);
Check("TLS registrar uses sips and 5061", tlsUri == "sips:pbx.example.invalid:5061", $"got {tlsUri}");
Check("TLS registrar parses as tls", SIPURI.ParseSIPURIRelaxed(tlsUri).Protocol == SIPProtocolsEnum.tls);

var tcpUri = SipSoftphone.BuildRegistrarUri("pbx.example.invalid", null, SipTransportMode.Tcp);
Check("TCP registrar parses as tcp", SIPURI.ParseSIPURIRelaxed(tcpUri).Protocol == SIPProtocolsEnum.tcp, $"got {tcpUri}");

var udpUri = SipSoftphone.BuildRegistrarUri("pbx.example.invalid", 5060, SipTransportMode.Udp);
Check("UDP registrar uses sip and 5060", udpUri == "sip:pbx.example.invalid:5060", $"got {udpUri}");

Console.WriteLine("Channels");

var tlsChannel = SipSoftphone.CreateChannel(SipTransportMode.Tls);
Check("TLS channel reports tls protocol", tlsChannel.SIPProtocol == SIPProtocolsEnum.tls);
Check("TLS channel reports secure", tlsChannel.IsSecure);
tlsChannel.Close();

var udpChannel = SipSoftphone.CreateChannel(SipTransportMode.Udp);
Check("UDP channel is not secure", !udpChannel.IsSecure);
udpChannel.Close();

Console.WriteLine("Certificate validation");

// The validator must accept only a clean chain. No bypass, in any build.
var validator = typeof(SipSoftphone).GetMethod(
    "ValidateServerCertificate",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
Check("validator exists", validator is not null);
if (validator is not null)
{
    bool Invoke(SslPolicyErrors errors) =>
        (bool)validator.Invoke(null, new object?[] { new object(), null, null, errors })!;

    Check("clean chain is accepted", Invoke(SslPolicyErrors.None));
    Check("name mismatch is rejected", !Invoke(SslPolicyErrors.RemoteCertificateNameMismatch));
    Check("untrusted chain is rejected", !Invoke(SslPolicyErrors.RemoteCertificateChainErrors));
    Check("missing certificate is rejected", !Invoke(SslPolicyErrors.RemoteCertificateNotAvailable));
}

if (failures.Count > 0)
{
    Console.WriteLine($"SIP transport smoke test FAILED: {string.Join(", ", failures)}");
    return 1;
}

Console.WriteLine("SIP transport smoke test passed: TLS is the default, secure addresses cannot be downgraded, and certificate validation rejects every policy error.");
return 0;
