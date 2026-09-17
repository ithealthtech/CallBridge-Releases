using System.Net;
using SIPSorcery.SIP;

namespace CallBridge.Desktop;

public enum SipIncomingCallDisposition
{
    RejectOffline,
    Primary,
    Waiting,
    Busy
}

public static class SipIncomingCallPolicy
{
    public static bool IsInitialInvite(SIPMethodsEnum method, string? toTag, string? replaces) =>
        method == SIPMethodsEnum.INVITE
        && string.IsNullOrWhiteSpace(toTag)
        && string.IsNullOrWhiteSpace(replaces);

    /// <summary>
    /// Environment switch that also admits calls from this PC (loopback), for local test callers only.
    /// Production installs leave it unset so only the phone system can place calls into CallBridge.
    /// </summary>
    public const string AllowLocalTestCallsVariable = "CALLBRIDGE_ALLOW_LOCAL_TEST_CALLS";

    public static bool LocalTestCallsAllowed =>
        Environment.GetEnvironmentVariable(AllowLocalTestCallsVariable) == "1";

    /// <summary>
    /// Incoming calls and voicemail notices are accepted only from the phone system: addresses its server
    /// name resolves to, or addresses that answered this client's registration. Loopback is accepted only
    /// when local test calls are explicitly allowed.
    /// </summary>
    public static bool IsTrustedSignalingSource(IPAddress? source, IEnumerable<IPAddress> trusted, bool allowLocalTestCalls)
    {
        if (source is null) return false;
        var normalized = source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source;
        if (IPAddress.IsLoopback(normalized)) return allowLocalTestCalls;
        return trusted.Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).Any(address => address.Equals(normalized));
    }
    public static SipIncomingCallDisposition Classify(
        bool registered,
        SipCallStatus call,
        bool hasPrimaryPendingCall,
        SipCallWaitingStatus waiting,
        bool hasWaitingAgent,
        bool transferActive)
    {
        if (!registered) return SipIncomingCallDisposition.RejectOffline;
        if (call.State is SipCallState.Idle or SipCallState.Failed && !hasPrimaryPendingCall)
            return SipIncomingCallDisposition.Primary;
        if (call.HasEstablishedCall
            && waiting.State is SipCallWaitingState.None or SipCallWaitingState.Failed
            && !hasWaitingAgent
            && !transferActive)
        {
            return SipIncomingCallDisposition.Waiting;
        }
        return SipIncomingCallDisposition.Busy;
    }
}
