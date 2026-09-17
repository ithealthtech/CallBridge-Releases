using SIPSorcery.Net;

namespace CallBridge.Desktop;

public sealed record SipCallQualitySnapshot(
    string Codec,
    long ReceivedPackets,
    double? InboundLossPercent,
    int? InboundPacketsLost,
    double? InboundJitterMilliseconds,
    double? OutboundLossPercent,
    int? OutboundPacketsLost,
    double? OutboundJitterMilliseconds,
    double? RoundTripMilliseconds)
{
    public static SipCallQualitySnapshot Empty { get; } = new("", 0, null, null, null, null, null, null, null);

    public bool HasMediaData => ReceivedPackets > 0
        || !string.IsNullOrWhiteSpace(Codec)
        || InboundLossPercent.HasValue
        || OutboundLossPercent.HasValue;

    public string ToUserSummary()
    {
        if (!HasMediaData) return "Call quality data will appear after media starts.";

        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(Codec)) values.Add(Codec);
        if (InboundLossPercent is { } inboundLoss) values.Add(FormattableString.Invariant($"receive loss {inboundLoss:0.0}%"));
        if (OutboundLossPercent is { } outboundLoss) values.Add(FormattableString.Invariant($"send loss {outboundLoss:0.0}%"));
        if (InboundJitterMilliseconds is { } jitter) values.Add(FormattableString.Invariant($"jitter {jitter:0} ms"));
        if (RoundTripMilliseconds is { } roundTrip) values.Add(FormattableString.Invariant($"round trip {roundTrip:0} ms"));
        return string.Join(" | ", values);
    }
}

public sealed class SipCallQualityMonitor
{
    private const double NtpFractionScale = 65536d;
    private const long NtpUnixEpochOffsetSeconds = 2_208_988_800L;
    private readonly object _sync = new();
    private string _codec = "";
    private int _clockRate = 8000;
    private long _receivedPackets;
    private ReportValues _inbound;
    private ReportValues _outbound;
    private double? _roundTripMilliseconds;

    public SipCallQualitySnapshot Current
    {
        get { lock (_sync) return CreateSnapshot(); }
    }

    public event Action<SipCallQualitySnapshot>? Changed;

    public void ObserveCodec(string codec, int clockRate)
    {
        SipCallQualitySnapshot snapshot;
        lock (_sync)
        {
            _codec = string.IsNullOrWhiteSpace(codec) ? "Unknown codec" : codec.Trim();
            _clockRate = clockRate > 0 ? clockRate : 8000;
            snapshot = CreateSnapshot();
        }
        Changed?.Invoke(snapshot);
    }

    public void ObserveReceivedPacket()
    {
        lock (_sync) _receivedPackets++;
    }

    public void ObserveLocalReport(RTCPCompoundPacket packet)
    {
        var report = GetReceptionReport(packet);
        if (report is null) return;

        SipCallQualitySnapshot snapshot;
        lock (_sync)
        {
            _inbound = ConvertReport(report, _clockRate);
            snapshot = CreateSnapshot();
        }
        Changed?.Invoke(snapshot);
    }

    public void ObserveRemoteReport(RTCPCompoundPacket packet) => ObserveRemoteReport(GetReceptionReport(packet));

    public void ObserveRemoteReport(ReceptionReportSample? report, DateTimeOffset? receivedAt = null)
    {
        if (report is null) return;

        SipCallQualitySnapshot snapshot;
        lock (_sync)
        {
            _outbound = ConvertReport(report, _clockRate);
            _roundTripMilliseconds = CalculateRoundTripMilliseconds(report, receivedAt ?? DateTimeOffset.UtcNow);
            snapshot = CreateSnapshot();
        }
        Changed?.Invoke(snapshot);
    }

    public void ObserveLocalReport(ReceptionReportSample? report)
    {
        if (report is null) return;

        SipCallQualitySnapshot snapshot;
        lock (_sync)
        {
            _inbound = ConvertReport(report, _clockRate);
            snapshot = CreateSnapshot();
        }
        Changed?.Invoke(snapshot);
    }

    private SipCallQualitySnapshot CreateSnapshot() => new(
        _codec,
        _receivedPackets,
        _inbound.LossPercent,
        _inbound.PacketsLost,
        _inbound.JitterMilliseconds,
        _outbound.LossPercent,
        _outbound.PacketsLost,
        _outbound.JitterMilliseconds,
        _roundTripMilliseconds);

    private static ReceptionReportSample? GetReceptionReport(RTCPCompoundPacket packet) =>
        packet.ReceiverReport?.ReceptionReports?.FirstOrDefault()
        ?? packet.SenderReport?.ReceptionReports?.FirstOrDefault();

    private static ReportValues ConvertReport(ReceptionReportSample report, int clockRate) => new(
        report.FractionLost * 100d / 256d,
        Math.Max(0, report.PacketsLost),
        report.Jitter * 1000d / Math.Max(1, clockRate));

    private static double? CalculateRoundTripMilliseconds(ReceptionReportSample report, DateTimeOffset receivedAt)
    {
        if (report.LastSenderReportTimestamp == 0) return null;

        var ntpSeconds = receivedAt.ToUnixTimeSeconds() + NtpUnixEpochOffsetSeconds;
        var millisecondsWithinSecond = receivedAt.ToUnixTimeMilliseconds() % 1000;
        var compactNow = (uint)(((ntpSeconds & 0xffff) << 16)
            | (long)Math.Round(millisecondsWithinSecond * NtpFractionScale / 1000d));
        var compactRoundTrip = unchecked(compactNow - report.LastSenderReportTimestamp - report.DelaySinceLastSenderReport);
        var milliseconds = compactRoundTrip * 1000d / NtpFractionScale;
        return milliseconds is >= 0 and <= 60_000 ? milliseconds : null;
    }

    private readonly record struct ReportValues(double? LossPercent, int? PacketsLost, double? JitterMilliseconds);
}
