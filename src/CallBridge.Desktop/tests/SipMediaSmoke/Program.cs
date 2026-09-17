using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorceryMedia.Windows;
using CallBridge.Desktop;
using System.Net.Security;

var stateMachine = new SipCallStateMachine();
var observedStates = new List<SipCallState>();
stateMachine.Changed += status => observedStates.Add(status.State);

AssertTransition(stateMachine, SipCallState.IncomingRinging, SipCallDirection.Inbound, "Support desk", true);
AssertTransition(stateMachine, SipCallState.OutboundDialing, SipCallDirection.Outbound, "101", false);
AssertTransition(stateMachine, SipCallState.Connecting, SipCallDirection.None, "", true);
AssertTransition(stateMachine, SipCallState.Connected, SipCallDirection.None, "", true);
AssertTransition(stateMachine, SipCallState.Held, SipCallDirection.None, "", true);
AssertTransition(stateMachine, SipCallState.Connected, SipCallDirection.None, "", true);
AssertTransition(stateMachine, SipCallState.Ending, SipCallDirection.None, "", true);
AssertTransition(stateMachine, SipCallState.Idle, SipCallDirection.None, "", true);

if (stateMachine.Current.Direction != SipCallDirection.None || stateMachine.Current.RemoteParty.Length != 0 || stateMachine.Current.RemoteNumber.Length != 0)
    throw new InvalidOperationException("Returning to idle must clear call direction and remote-party data.");
if (observedStates.Contains(SipCallState.OutboundDialing))
    throw new InvalidOperationException("An illegal call transition must not publish a state change.");

var transferState = new SipTransferStateMachine();
AssertTransferTransition(transferState, SipTransferState.ConsultationDialing, "202", true);
AssertTransferTransition(transferState, SipTransferState.ConsultationRinging, "", true);
AssertTransferTransition(transferState, SipTransferState.Completing, "", false);
AssertTransferTransition(transferState, SipTransferState.ConsultationConnected, "", true);
AssertTransferTransition(transferState, SipTransferState.Completing, "", true);
if (transferState.Current.CanCancel)
    throw new InvalidOperationException("An attended transfer cannot be cancelled after final transfer signaling has started.");
AssertTransferTransition(transferState, SipTransferState.ConsultationConnected, "", true);
if (!transferState.Current.CanComplete || !transferState.Current.CanCancel)
    throw new InvalidOperationException("A connected consultation must support completion or cancellation.");
AssertTransferTransition(transferState, SipTransferState.None, "", true);
if (transferState.Current.Destination.Length != 0 || transferState.Current.IsActive)
    throw new InvalidOperationException("Completing or cancelling a transfer must clear consultation state.");

var waitingState = new SipCallWaitingStateMachine();
AssertWaitingTransition(waitingState, SipCallWaitingState.Ringing, "Second caller", "202", "First caller", "", true);
if (!waitingState.Current.CanAnswer || !waitingState.Current.CanDecline)
    throw new InvalidOperationException("A ringing waiting call must support answer and decline.");
AssertWaitingTransition(waitingState, SipCallWaitingState.Answering, "", "", "", "", true);
AssertWaitingTransition(waitingState, SipCallWaitingState.TwoCalls, "", "", "Second caller", "First caller", true);
if (!waitingState.Current.CanSwap || waitingState.Current.ActiveParty != "Second caller" || waitingState.Current.HeldParty != "First caller")
    throw new InvalidOperationException("Two connected calls must expose active and held identities for switching.");
AssertWaitingTransition(waitingState, SipCallWaitingState.TwoCalls, "", "", "First caller", "Second caller", true);
AssertWaitingTransition(waitingState, SipCallWaitingState.Ringing, "Third caller", "303", "", "", false);
AssertWaitingTransition(waitingState, SipCallWaitingState.None, "", "", "", "", true);
if (waitingState.Current.HasPendingCall || waitingState.Current.HasTwoCalls || waitingState.Current.IncomingParty.Length != 0)
    throw new InvalidOperationException("Clearing call waiting must remove all secondary-call identity and action state.");

var lifecycleStates = Enum.GetValues<SipCallLifecycleState>();
var lifecycleApiStates = lifecycleStates
    .Select(state => new SipCallLifecycleEvent("call-a", SipCallDirection.Inbound, "202", state, DateTimeOffset.UtcNow).ApiState)
    .ToArray();
if (lifecycleApiStates.Distinct(StringComparer.Ordinal).Count() != lifecycleStates.Length
    || lifecycleApiStates.Any(string.IsNullOrWhiteSpace)
    || lifecycleApiStates.Contains("connected", StringComparer.Ordinal)
    || lifecycleApiStates.Contains("call-switched", StringComparer.Ordinal))
{
    throw new InvalidOperationException("SIP lifecycle events must map one-to-one onto canonical service states.");
}
var terminalStates = lifecycleStates
    .Where(state => new SipCallLifecycleEvent("call-a", SipCallDirection.Inbound, "202", state, DateTimeOffset.UtcNow).IsTerminal)
    .ToHashSet();
if (!terminalStates.SetEquals([
        SipCallLifecycleState.Transfer,
        SipCallLifecycleState.Hangup,
        SipCallLifecycleState.Ended,
        SipCallLifecycleState.Missed,
        SipCallLifecycleState.Failed,
        SipCallLifecycleState.Declined]))
{
    throw new InvalidOperationException("SIP lifecycle terminal-state classification is incomplete.");
}

if (!SipIncomingCallPolicy.IsInitialInvite(SIPMethodsEnum.INVITE, null, null)
    || SipIncomingCallPolicy.IsInitialInvite(SIPMethodsEnum.INVITE, "dialog-tag", null)
    || SipIncomingCallPolicy.IsInitialInvite(SIPMethodsEnum.INVITE, null, "call-id;to-tag=a;from-tag=b")
    || SipIncomingCallPolicy.IsInitialInvite(SIPMethodsEnum.OPTIONS, null, null))
{
    throw new InvalidOperationException("Initial INVITE dispatch must exclude in-dialog, transfer, and non-INVITE requests.");
}
var idleCall = new SipCallStatus(SipCallState.Idle, SipCallDirection.None, "", "", "Ready");
var connectedCall = new SipCallStatus(SipCallState.Connected, SipCallDirection.Outbound, "First caller", "101", "Connected");
var noWaiting = new SipCallWaitingStatus(SipCallWaitingState.None, "", "", "", "", "Ready");
if (SipIncomingCallPolicy.Classify(false, idleCall, false, noWaiting, false, false) != SipIncomingCallDisposition.RejectOffline
    || SipIncomingCallPolicy.Classify(true, idleCall, false, noWaiting, false, false) != SipIncomingCallDisposition.Primary
    || SipIncomingCallPolicy.Classify(true, connectedCall, false, noWaiting, false, false) != SipIncomingCallDisposition.Waiting
    || SipIncomingCallPolicy.Classify(true, connectedCall, false, noWaiting, false, true) != SipIncomingCallDisposition.Busy
    || SipIncomingCallPolicy.Classify(true, connectedCall, false, waitingState.Current with { State = SipCallWaitingState.Ringing }, true, false) != SipIncomingCallDisposition.Busy)
{
    throw new InvalidOperationException("Incoming call classification must preserve registration, transfer, and single-waiting-call limits.");
}

AssertSipTransport("UDP", SipSignalingTransport.Udp, "sip:pbx.example.test", "sip:101@pbx.example.test");
AssertSipTransport("TCP", SipSignalingTransport.Tcp, "sip:pbx.example.test;transport=tcp", "sip:101@pbx.example.test;transport=tcp");
AssertSipTransport("TLS", SipSignalingTransport.Tls, "sips:pbx.example.test", "sips:101@pbx.example.test");
if (SipTransportProfile.TryParse("invalid", out _))
    throw new InvalidOperationException("Unsupported SIP transport values must be rejected.");
if (!SipRegistrationRecoveryPolicy.CanReconnect(true, true, false, SipCallState.Idle)
    || !SipRegistrationRecoveryPolicy.CanReconnect(true, true, false, SipCallState.Failed)
    || SipRegistrationRecoveryPolicy.CanReconnect(false, true, false, SipCallState.Idle)
    || SipRegistrationRecoveryPolicy.CanReconnect(true, false, false, SipCallState.Idle)
    || SipRegistrationRecoveryPolicy.CanReconnect(true, true, true, SipCallState.Idle)
    || SipRegistrationRecoveryPolicy.CanReconnect(true, true, false, SipCallState.Connected))
{
    throw new InvalidOperationException("SIP registration recovery policy does not preserve user intent and active calls.");
}
if (!SipRegistrationStartupPolicy.ShouldStart(true, false, false, true, true, false)
    || SipRegistrationStartupPolicy.ShouldStart(false, false, false, true, true, false)
    || SipRegistrationStartupPolicy.ShouldStart(true, true, false, true, true, false)
    || SipRegistrationStartupPolicy.ShouldStart(true, false, true, true, true, false)
    || SipRegistrationStartupPolicy.ShouldStart(true, false, false, false, true, false)
    || SipRegistrationStartupPolicy.ShouldStart(true, false, false, true, false, false)
    || SipRegistrationStartupPolicy.ShouldStart(true, false, false, true, true, true))
{
    throw new InvalidOperationException("SIP startup registration policy does not preserve enabled, disabled, and shutdown intent.");
}
var voicemail = VoicemailStatus.Parse("Messages-Waiting: yes\r\nMessage-Account: sip:214@pbx.example\r\nVoice-Message: 2/5 (0/1)\r\n");
if (voicemail is not { Known: true, MessagesWaiting: true, NewMessages: 2, SavedMessages: 5 } || voicemail.Summary != "2 new · 5 saved")
    throw new InvalidOperationException("Voicemail message-summary counts were not parsed.");
if (VoicemailStatus.Parse("Messages-Waiting: no\r\n") is not { MessagesWaiting: false, NewMessages: 0 } || VoicemailStatus.Parse("hello") is not null)
    throw new InvalidOperationException("Voicemail message-summary edge cases were not handled.");
const string busySlot = """<?xml version="1.0"?><dialog-info xmlns="urn:ietf:params:xml:ns:dialog-info" version="1" state="full" entity="sip:701@pbx"><dialog id="a"><state>confirmed</state></dialog></dialog-info>""";
const string openSlot = """<?xml version="1.0"?><dialog-info xmlns="urn:ietf:params:xml:ns:dialog-info" version="2" state="full" entity="sip:701@pbx"></dialog-info>""";
const string hostileSlot = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///c:/windows/win.ini">]><dialog-info>&e;</dialog-info>""";
if (ParkSlotStatus.ParseDialogInfo(busySlot) != ParkSlotState.Occupied || ParkSlotStatus.ParseDialogInfo(openSlot) != ParkSlotState.Open
    || ParkSlotStatus.ParseDialogInfo(hostileSlot) != ParkSlotState.Unknown || ParkSlotStatus.ParseDialogInfo("not xml") != ParkSlotState.Unknown)
{
    throw new InvalidOperationException("Park slot dialog-info parsing must detect busy and open slots and reject DTDs.");
}
if (!SipFeatureCodes.TryParseSlots("701-703, 710", out var parkSlots, out _) || !parkSlots.SequenceEqual(new[] { "701", "702", "703", "710" })
    || SipFeatureCodes.TryParseSlots("701;drop", out _, out _) || !SipFeatureCodes.IsValidDialString("*97") || SipFeatureCodes.IsValidDialString("97@evil"))
{
    throw new InvalidOperationException("Park slot and feature code validation is wrong.");
}
if (WindowsIncomingCallNotifier.ClassifyMessage(new IntPtr(0x0202)) != TrayIconAction.OpenFlyout
    || WindowsIncomingCallNotifier.ClassifyMessage(new IntPtr(0x0205)) != TrayIconAction.OpenFlyout
    || WindowsIncomingCallNotifier.ClassifyMessage(new IntPtr(0x0405)) != TrayIconAction.ActivateWindow
    || WindowsIncomingCallNotifier.ClassifyMessage(new IntPtr(0x0200)) != TrayIconAction.None)
{
    throw new InvalidOperationException("Tray icon and incoming-call notification messages are not classified correctly.");
}
if (SipCallProgress.DescribeProvisionalResponse(183, true) != "Early media"
    || SipCallProgress.DescribeProvisionalResponse(183, false) != "Ringing"
    || SipCallProgress.DescribeProvisionalResponse(180, true) != "Ringing")
{
    throw new InvalidOperationException("SIP provisional responses are not classified correctly for early media.");
}
foreach (var profileName in SipCodecProfile.DisplayNames)
{
    var profile = SipCodecProfile.Resolve(profileName);
    if (profile.AllowedCodecs.Contains(SIPSorceryMedia.Abstractions.AudioCodecsEnum.G729))
        throw new InvalidOperationException($"{profileName} must not advertise unapproved G.729 media.");
    var profileAudio = new WindowsAudioEndPoint(new AudioEncoder(includeOpus: profile.IncludeOpus));
    profileAudio.RestrictFormats(profile.Allows);
    var offeredCodecs = profileAudio.GetAudioSourceFormats().Select(format => format.Codec).ToList();
    if (offeredCodecs.Count == 0 || offeredCodecs.Any(codec => !profile.AllowedCodecs.Contains(codec)))
        throw new InvalidOperationException($"{profileName} produced an invalid codec offer.");
    await profileAudio.CloseAudio();
    await profileAudio.CloseAudioSink();
}
var axionProfile = SipCodecProfile.Resolve(SipCodecProfile.AxionName);
if (!axionProfile.AllowedCodecs.SetEquals([SIPSorceryMedia.Abstractions.AudioCodecsEnum.PCMU]))
    throw new InvalidOperationException("The Axion codec profile must offer PCMU only.");
if (!SipTransportProfile.AcceptsCertificateErrors(SslPolicyErrors.None)
    || SipTransportProfile.AcceptsCertificateErrors(SslPolicyErrors.RemoteCertificateNameMismatch)
    || SipTransportProfile.AcceptsCertificateErrors(SslPolicyErrors.RemoteCertificateChainErrors)
    || SipTransportProfile.AcceptsCertificateErrors(SslPolicyErrors.RemoteCertificateNotAvailable))
{
    throw new InvalidOperationException("SIP TLS must accept only certificates with no policy errors.");
}
foreach (var transportMode in Enum.GetValues<SipSignalingTransport>())
{
    var sipTransport = new SIPSorcery.SIP.SIPTransport();
    sipTransport.AddSIPChannel(SipTransportProfile.CreateChannel(transportMode));
    sipTransport.Shutdown();
}

var microphones = WindowsAudioDeviceCatalog.GetMicrophones();
var speakers = WindowsAudioDeviceCatalog.GetSpeakers();
AssertAudioCatalog(microphones, "microphone");
AssertAudioCatalog(speakers, "speaker");

var legacyDefault = WindowsAudioDeviceCatalog.Resolve(microphones, WindowsAudioDeviceCatalog.LegacyDefaultDeviceName, "microphone");
if (legacyDefault.FellBack || legacyDefault.Device.DeviceIndex != -1)
    throw new InvalidOperationException("The legacy default-device setting must migrate without a warning.");
var missing = WindowsAudioDeviceCatalog.Resolve(speakers, "windows:output:missing", "speaker");
if (!missing.FellBack || missing.Device.DeviceIndex != -1 || string.IsNullOrWhiteSpace(missing.UserMessage))
    throw new InvalidOperationException("A missing audio device must fall back to the Windows default with a user message.");

var microphone = WindowsAudioDeviceCatalog.ResolveMicrophone(WindowsAudioDeviceCatalog.DefaultDeviceId);
var speaker = WindowsAudioDeviceCatalog.ResolveSpeaker(WindowsAudioDeviceCatalog.DefaultDeviceId);
var audio = new WindowsAudioEndPoint(new AudioEncoder(), speaker.Device.DeviceIndex, microphone.Device.DeviceIndex);
var media = SipMediaSessionPolicy.CreateAudioSession();
if (media.AcceptRtpFromAny)
    throw new InvalidOperationException("CallBridge must reject RTP source changes from arbitrary network endpoints.");

var qualityMonitor = new SipCallQualityMonitor();
qualityMonitor.ObserveCodec("PCMU", 8000);
qualityMonitor.ObserveReceivedPacket();
qualityMonitor.ObserveLocalReport(new ReceptionReportSample(1, 26, 2, 100, 80, 0, 0));
qualityMonitor.ObserveRemoteReport(new ReceptionReportSample(2, 13, 1, 100, 40, 0, 0));
var quality = qualityMonitor.Current;
if (quality.Codec != "PCMU"
    || quality.ReceivedPackets != 1
    || quality.InboundPacketsLost != 2
    || quality.OutboundPacketsLost != 1
    || Math.Abs(quality.InboundLossPercent.GetValueOrDefault() - 10.15625) > 0.001
    || Math.Abs(quality.InboundJitterMilliseconds.GetValueOrDefault() - 10) > 0.001
    || !quality.ToUserSummary().Contains("PCMU", StringComparison.Ordinal))
{
    throw new InvalidOperationException("Sanitized SIP call-quality metrics were not calculated correctly.");
}

var reportReceivedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_500);
var ntpSeconds = reportReceivedAt.ToUnixTimeSeconds() + 2_208_988_800L;
var compactNow = (uint)(((ntpSeconds & 0xffff) << 16) | 32_768L);
var remoteDelay = (uint)Math.Round(0.05 * 65_536);
var expectedRoundTrip = (uint)Math.Round(0.1 * 65_536);
qualityMonitor.ObserveRemoteReport(
    new ReceptionReportSample(2, 0, 0, 100, 0, unchecked(compactNow - remoteDelay - expectedRoundTrip), remoteDelay),
    reportReceivedAt);
if (Math.Abs(qualityMonitor.Current.RoundTripMilliseconds.GetValueOrDefault() - 100) > 0.1)
    throw new InvalidOperationException("RTCP sender-report timing did not produce the expected round-trip measurement.");
media.addTrack(new MediaStreamTrack(audio.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv));
audio.OnAudioSourceEncodedSample += media.SendAudio;
media.Close("smoke test");
await audio.CloseAudio();
await audio.CloseAudioSink();
Console.WriteLine("SIP Windows audio/media session initialized successfully.");

static void AssertAudioCatalog(IReadOnlyList<WindowsAudioDeviceOption> devices, string label)
{
    if (devices.Count == 0 || !devices[0].IsDefault || devices[0].DeviceIndex != -1)
        throw new InvalidOperationException($"The {label} catalog must start with the Windows default device.");
    if (devices.Select(device => device.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != devices.Count)
        throw new InvalidOperationException($"The {label} catalog contains duplicate device identifiers.");
}

static void AssertSipTransport(string displayName, SipSignalingTransport expected, string registrar, string destination)
{
    if (!SipTransportProfile.TryParse(displayName, out var actual) || actual != expected)
        throw new InvalidOperationException($"Could not parse {displayName} SIP transport.");
    var registrarUri = SipTransportProfile.BuildRegistrarUri("pbx.example.test", actual).ToString();
    var destinationUri = SipTransportProfile.BuildDestinationUri("101", "pbx.example.test", actual).ToString();
    if (!registrarUri.Equals(registrar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{displayName} registrar URI expected {registrar}, got {registrarUri}.");
    if (!destinationUri.Equals(destination, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{displayName} destination URI expected {destination}, got {destinationUri}.");
}

static void AssertTransition(
    SipCallStateMachine stateMachine,
    SipCallState state,
    SipCallDirection direction,
    string remoteParty,
    bool expected)
{
    var actual = stateMachine.TryTransition(state, direction, remoteParty, state.ToString());
    if (actual != expected)
        throw new InvalidOperationException($"Transition to {state} expected {expected}, got {actual}.");
}

static void AssertTransferTransition(SipTransferStateMachine stateMachine, SipTransferState state, string destination, bool expected)
{
    var actual = stateMachine.TryTransition(state, destination, state.ToString());
    if (actual != expected)
        throw new InvalidOperationException($"Transfer transition to {state} expected {expected}, got {actual}.");
}

static void AssertWaitingTransition(
    SipCallWaitingStateMachine stateMachine,
    SipCallWaitingState state,
    string incomingParty,
    string incomingNumber,
    string activeParty,
    string heldParty,
    bool expected)
{
    var actual = stateMachine.TryTransition(state, incomingParty, incomingNumber, activeParty, heldParty, state.ToString());
    if (actual != expected)
        throw new InvalidOperationException($"Call-waiting transition to {state} expected {expected}, got {actual}.");
}
