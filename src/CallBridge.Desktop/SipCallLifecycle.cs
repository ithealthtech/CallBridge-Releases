namespace CallBridge.Desktop;

public enum SipCallLifecycleState
{
    Ringing,
    Started,
    Answered,
    Hold,
    Resume,
    Transfer,
    Hangup,
    Ended,
    Missed,
    Failed,
    Declined
}

public sealed record SipCallLifecycleEvent(
    string CallId,
    SipCallDirection Direction,
    string RemoteNumber,
    SipCallLifecycleState State,
    DateTimeOffset OccurredAt,
    string EventId = "")
{
    public string ApiState => State switch
    {
        SipCallLifecycleState.Ringing => "ringing",
        SipCallLifecycleState.Started => "started",
        SipCallLifecycleState.Answered => "answered",
        SipCallLifecycleState.Hold => "hold",
        SipCallLifecycleState.Resume => "resume",
        SipCallLifecycleState.Transfer => "transfer",
        SipCallLifecycleState.Hangup => "hangup",
        SipCallLifecycleState.Ended => "ended",
        SipCallLifecycleState.Missed => "missed",
        SipCallLifecycleState.Failed => "failed",
        SipCallLifecycleState.Declined => "declined",
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, "Unsupported SIP lifecycle state.")
    };

    public bool IsTerminal => State is
        SipCallLifecycleState.Transfer or
        SipCallLifecycleState.Hangup or
        SipCallLifecycleState.Ended or
        SipCallLifecycleState.Missed or
        SipCallLifecycleState.Failed or
        SipCallLifecycleState.Declined;
}
