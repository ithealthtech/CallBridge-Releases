namespace CallBridge.Desktop;

public static class SipCallProgress
{
    public static string DescribeProvisionalResponse(int statusCode, bool hasSessionDescription) =>
        statusCode == 183 && hasSessionDescription ? "Early media" : "Ringing";
}
