using SIPSorcery.Net;

namespace CallBridge.Desktop;

public static class SipMediaSessionPolicy
{
    public static RTPSession CreateAudioSession()
    {
        return new RTPSession(false, false, false)
        {
            // Keep SIPSorcery's constrained NAT learning, but never permit arbitrary
            // network endpoints to replace the negotiated RTP source.
            AcceptRtpFromAny = false
        };
    }
}
