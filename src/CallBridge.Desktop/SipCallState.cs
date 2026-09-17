namespace CallBridge.Desktop;

public enum SipCallState
{
    Idle,
    OutboundDialing,
    OutboundRinging,
    IncomingRinging,
    Connecting,
    Connected,
    Held,
    Ending,
    Failed
}

public enum SipCallDirection
{
    None,
    Inbound,
    Outbound
}

public sealed record SipCallStatus(
    SipCallState State,
    SipCallDirection Direction,
    string RemoteParty,
    string RemoteNumber,
    string Detail)
{
    public bool HasEstablishedCall => State is SipCallState.Connected or SipCallState.Held;
    public bool HasPendingIncomingCall => State == SipCallState.IncomingRinging;
}

public sealed class SipCallStateMachine
{
    private readonly object _sync = new();
    private SipCallStatus _current = new(SipCallState.Idle, SipCallDirection.None, "", "", "Ready");

    public SipCallStatus Current
    {
        get { lock (_sync) return _current; }
    }

    public event Action<SipCallStatus>? Changed;

    public bool TryTransition(SipCallState next, SipCallDirection direction, string remoteParty, string detail, string remoteNumber = "")
    {
        SipCallStatus updated;
        lock (_sync)
        {
            if (!IsAllowed(_current.State, next)) return false;

            var effectiveDirection = direction == SipCallDirection.None ? _current.Direction : direction;
            var effectiveRemoteParty = string.IsNullOrWhiteSpace(remoteParty) ? _current.RemoteParty : remoteParty;
            var effectiveRemoteNumber = string.IsNullOrWhiteSpace(remoteNumber) ? _current.RemoteNumber : remoteNumber;
            if (next == SipCallState.Idle)
            {
                effectiveDirection = SipCallDirection.None;
                effectiveRemoteParty = "";
                effectiveRemoteNumber = "";
            }

            updated = new(next, effectiveDirection, effectiveRemoteParty, effectiveRemoteNumber, detail);
            _current = updated;
        }

        Changed?.Invoke(updated);
        return true;
    }

    private static bool IsAllowed(SipCallState current, SipCallState next)
    {
        if (current == next) return true;

        return current switch
        {
            SipCallState.Idle => next is SipCallState.OutboundDialing or SipCallState.IncomingRinging,
            SipCallState.OutboundDialing => next is SipCallState.OutboundRinging or SipCallState.Connected or SipCallState.Ending or SipCallState.Failed,
            SipCallState.OutboundRinging => next is SipCallState.Connected or SipCallState.Ending or SipCallState.Failed,
            SipCallState.IncomingRinging => next is SipCallState.Connecting or SipCallState.Ending or SipCallState.Failed,
            SipCallState.Connecting => next is SipCallState.Connected or SipCallState.Ending or SipCallState.Failed,
            SipCallState.Connected => next is SipCallState.Held or SipCallState.Ending or SipCallState.Failed,
            SipCallState.Held => next is SipCallState.Connected or SipCallState.Ending or SipCallState.Failed,
            SipCallState.Ending => next is SipCallState.Idle or SipCallState.Failed,
            SipCallState.Failed => next is SipCallState.Idle or SipCallState.OutboundDialing or SipCallState.IncomingRinging,
            _ => false
        };
    }
}
