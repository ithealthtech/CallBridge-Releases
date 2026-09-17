namespace CallBridge.Desktop;

public enum SipTransferState
{
    None,
    ConsultationDialing,
    ConsultationRinging,
    ConsultationConnected,
    Completing,
    Failed
}

public sealed record SipTransferStatus(SipTransferState State, string Destination, string Detail)
{
    public bool IsActive => State is not (SipTransferState.None or SipTransferState.Failed);
    public bool CanComplete => State == SipTransferState.ConsultationConnected;
    public bool CanCancel => State is SipTransferState.ConsultationDialing or SipTransferState.ConsultationRinging or SipTransferState.ConsultationConnected;
}

public sealed class SipTransferStateMachine
{
    private readonly object _sync = new();
    private SipTransferStatus _current = new(SipTransferState.None, "", "Ready");

    public SipTransferStatus Current
    {
        get { lock (_sync) return _current; }
    }

    public event Action<SipTransferStatus>? Changed;

    public bool TryTransition(SipTransferState next, string destination = "", string detail = "")
    {
        SipTransferStatus updated;
        lock (_sync)
        {
            if (!IsAllowed(_current.State, next)) return false;
            var effectiveDestination = string.IsNullOrWhiteSpace(destination) ? _current.Destination : destination;
            if (next == SipTransferState.None) effectiveDestination = "";
            updated = new(next, effectiveDestination, detail);
            _current = updated;
        }

        Changed?.Invoke(updated);
        return true;
    }

    private static bool IsAllowed(SipTransferState current, SipTransferState next)
    {
        if (current == next) return true;
        return current switch
        {
            SipTransferState.None => next == SipTransferState.ConsultationDialing,
            SipTransferState.ConsultationDialing => next is SipTransferState.ConsultationRinging or SipTransferState.ConsultationConnected or SipTransferState.Failed or SipTransferState.None,
            SipTransferState.ConsultationRinging => next is SipTransferState.ConsultationConnected or SipTransferState.Failed or SipTransferState.None,
            SipTransferState.ConsultationConnected => next is SipTransferState.Completing or SipTransferState.Failed or SipTransferState.None,
            SipTransferState.Completing => next is SipTransferState.ConsultationConnected or SipTransferState.Failed or SipTransferState.None,
            SipTransferState.Failed => next is SipTransferState.None or SipTransferState.ConsultationDialing,
            _ => false
        };
    }
}
