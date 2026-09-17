using System.Net;
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
Check("new installs default to TLS", new AppSettings().SipTransport == "TLS");
Check("unknown transport is rejected", !SipTransportProfile.TryParse("carrier-pigeon", out _));
Check("explicit UDP is honoured", SipTransportProfile.TryParse("udp", out var udp) && udp == SipSignalingTransport.Udp);
Check("explicit TCP is honoured", SipTransportProfile.TryParse("TCP", out var tcp) && tcp == SipSignalingTransport.Tcp);

Console.WriteLine("Secure addresses cannot be downgraded");
Check("sips: scheme upgrades UDP to TLS", SipTransportProfile.ResolveTransport("sips:pbx.example.invalid", SipSignalingTransport.Udp) == SipSignalingTransport.Tls);
Check("port 5061 upgrades UDP to TLS", SipTransportProfile.ResolveTransport("pbx.example.invalid:5061", SipSignalingTransport.Udp) == SipSignalingTransport.Tls);
Check("sip:user@host:5061 upgrades TCP to TLS", SipTransportProfile.ResolveTransport("sip:alice@pbx.example.invalid:5061", SipSignalingTransport.Tcp) == SipSignalingTransport.Tls);
Check("explicit UDP on 5060 stays UDP", SipTransportProfile.ResolveTransport("pbx.example.invalid:5060", SipSignalingTransport.Udp) == SipSignalingTransport.Udp);
Check("bare host keeps configured transport", SipTransportProfile.ResolveTransport("pbx.example.invalid", SipSignalingTransport.Tcp) == SipSignalingTransport.Tcp);

Console.WriteLine("Registrar URI");
var tlsUri = SipTransportProfile.BuildRegistrarUri("pbx.example.invalid", SipSignalingTransport.Tls);
Check("TLS registrar uses sips", tlsUri.Scheme == SIPSchemesEnum.sips && tlsUri.Protocol == SIPProtocolsEnum.tls, $"got {tlsUri}");
var tcpUri = SipTransportProfile.BuildRegistrarUri("pbx.example.invalid", SipSignalingTransport.Tcp);
Check("TCP registrar uses tcp", tcpUri.Protocol == SIPProtocolsEnum.tcp, $"got {tcpUri}");
var withUser = SipTransportProfile.BuildRegistrarUri("sip:alice@pbx.example.invalid", SipSignalingTransport.Udp);
Check("user part is stripped from registrar", string.IsNullOrEmpty(withUser.User) && withUser.Host.StartsWith("pbx.example.invalid", StringComparison.Ordinal), $"got {withUser}");

Console.WriteLine("Channels");
var tlsChannel = SipTransportProfile.CreateChannel(SipSignalingTransport.Tls);
Check("TLS channel reports tls protocol", tlsChannel.SIPProtocol == SIPProtocolsEnum.tls);
Check("TLS channel reports secure", tlsChannel.IsSecure);
tlsChannel.Close();
var udpChannel = SipTransportProfile.CreateChannel(SipSignalingTransport.Udp);
Check("UDP channel is not secure", !udpChannel.IsSecure);
udpChannel.Close();

Console.WriteLine("Certificate validation");
Check("clean chain is accepted", SipTransportProfile.AcceptsCertificateErrors(SslPolicyErrors.None));
Check("name mismatch is rejected", !SipTransportProfile.AcceptsCertificateErrors(SslPolicyErrors.RemoteCertificateNameMismatch));
Check("untrusted chain is rejected", !SipTransportProfile.AcceptsCertificateErrors(SslPolicyErrors.RemoteCertificateChainErrors));
Check("missing certificate is rejected", !SipTransportProfile.AcceptsCertificateErrors(SslPolicyErrors.RemoteCertificateNotAvailable));

Console.WriteLine("Incoming signaling source");
IPAddress[] phoneSystem = [IPAddress.Parse("203.0.113.10"), IPAddress.Parse("2001:db8::10")];
Check("phone system address is trusted", SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.Parse("203.0.113.10"), phoneSystem, false));
Check("IPv4-mapped phone system address is trusted", SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.Parse("::ffff:203.0.113.10"), phoneSystem, false));
Check("IPv6 phone system address is trusted", SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.Parse("2001:db8::10"), phoneSystem, false));
Check("other internet address is refused", !SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.Parse("198.51.100.7"), phoneSystem, false));
Check("LAN address is refused", !SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.Parse("192.168.1.50"), phoneSystem, false));
Check("loopback is refused by default", !SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.Loopback, phoneSystem, false));
Check("IPv6 loopback is refused by default", !SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.IPv6Loopback, phoneSystem, false));
Check("loopback is allowed with the test switch", SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.Loopback, phoneSystem, true));
Check("test switch does not open other sources", !SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.Parse("198.51.100.7"), phoneSystem, true));
Check("unknown source is refused", !SipIncomingCallPolicy.IsTrustedSignalingSource(null, phoneSystem, true));
Check("nothing is trusted before registration", !SipIncomingCallPolicy.IsTrustedSignalingSource(IPAddress.Parse("203.0.113.10"), [], false));
if (failures.Count > 0)
{
    Console.WriteLine($"SIP transport smoke test FAILED: {string.Join(", ", failures)}");
    return 1;
}

Console.WriteLine("SIP transport smoke test passed: new installs default to TLS, secure addresses cannot be downgraded, certificate validation rejects every policy error, and incoming calls are accepted only from the phone system.");
return 0;