namespace CallBridge.Desktop;

public enum SipCallWaitingState
{
    None,
    Ringing,
    Answering,
    TwoCalls,
    Failed
}

public sealed record SipCallWaitingStatus(
    SipCallWaitingState State,
    string IncomingParty,
    string IncomingNumber,
    string ActiveParty,
    string HeldParty,
    string Detail)
{
    public bool HasPendingCall => State is SipCallWaitingState.Ringing or SipCallWaitingState.Answering;
    public bool HasTwoCalls => State == SipCallWaitingState.TwoCalls;
    public bool CanAnswer => State == SipCallWaitingState.Ringing;
    public bool CanDecline => State == SipCallWaitingState.Ringing;
    public bool CanSwap => State == SipCallWaitingState.TwoCalls;
}

public sealed class SipCallWaitingStateMachine
{
    private readonly object _sync = new();
    private SipCallWaitingStatus _current = new(SipCallWaitingState.None, "", "", "", "", "Ready");

    public SipCallWaitingStatus Current
    {
        get { lock (_sync) return _current; }
    }

    public event Action<SipCallWaitingStatus>? Changed;

    public bool TryTransition(
        SipCallWaitingState next,
        string incomingParty = "",
        string incomingNumber = "",
        string activeParty = "",
        string heldParty = "",
        string detail = "")
    {
        SipCallWaitingStatus updated;
        lock (_sync)
        {
            if (!IsAllowed(_current.State, next)) return false;

            if (next == SipCallWaitingState.None)
            {
                updated = new(next, "", "", "", "", detail);
            }
            else
            {
                updated = new(
                    next,
                    ValueOrExisting(incomingParty, _current.IncomingParty),
                    ValueOrExisting(incomingNumber, _current.IncomingNumber),
                    ValueOrExisting(activeParty, _current.ActiveParty),
                    ValueOrExisting(heldParty, _current.HeldParty),
                    detail);
            }
            _current = updated;
        }

        Changed?.Invoke(updated);
        return true;
    }

    private static string ValueOrExisting(string value, string existing) =>
        string.IsNullOrWhiteSpace(value) ? existing : value;

    private static bool IsAllowed(SipCallWaitingState current, SipCallWaitingState next)
    {
        if (current == next) return true;
        return current switch
        {
            SipCallWaitingState.None => next == SipCallWaitingState.Ringing,
            SipCallWaitingState.Ringing => next is SipCallWaitingState.Answering or SipCallWaitingState.None or SipCallWaitingState.Failed,
            SipCallWaitingState.Answering => next is SipCallWaitingState.TwoCalls or SipCallWaitingState.None or SipCallWaitingState.Failed,
            SipCallWaitingState.TwoCalls => next is SipCallWaitingState.None or SipCallWaitingState.Failed,
            SipCallWaitingState.Failed => next is SipCallWaitingState.None or SipCallWaitingState.Ringing,
            _ => false
        };
    }
}
