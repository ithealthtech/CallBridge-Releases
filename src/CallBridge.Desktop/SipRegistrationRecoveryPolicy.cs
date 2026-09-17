namespace CallBridge.Desktop;

public static class SipRegistrationRecoveryPolicy
{
    public static bool CanReconnect(
        bool registrationRequested,
        bool hasValidSettings,
        bool shutdownStarted,
        SipCallState callState) =>
        registrationRequested
        && hasValidSettings
        && !shutdownStarted
        && callState is SipCallState.Idle or SipCallState.Failed;
}
