namespace CallBridge.Desktop;

public static class SipRegistrationStartupPolicy
{
    public static bool ShouldStart(
        bool registrationEnabled,
        bool registrationRequested,
        bool alreadyRegistered,
        bool providerSupported,
        bool hasValidSettings,
        bool shutdownStarted) =>
        registrationEnabled
        && !registrationRequested
        && !alreadyRegistered
        && providerSupported
        && hasValidSettings
        && !shutdownStarted;
}
