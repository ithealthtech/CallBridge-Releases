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
