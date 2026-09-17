using SIPSorceryMedia.Abstractions;

namespace CallBridge.Desktop;

public sealed class SipCodecProfile
{
    public const string CompatibilityName = "G.711 compatibility";
    public const string WidebandName = "Wideband (Opus + G.722)";
    public const string AxionName = "Axion G.711 (PCMU)";

    private readonly HashSet<AudioCodecsEnum> _allowedCodecs;

    private SipCodecProfile(string name, bool includeOpus, params AudioCodecsEnum[] allowedCodecs)
    {
        Name = name;
        IncludeOpus = includeOpus;
        _allowedCodecs = [.. allowedCodecs];
    }

    public string Name { get; }
    public bool IncludeOpus { get; }
    public IReadOnlySet<AudioCodecsEnum> AllowedCodecs => _allowedCodecs;
    public bool Allows(AudioFormat format) => _allowedCodecs.Contains(format.Codec);

    public static IReadOnlyList<string> DisplayNames { get; } = [CompatibilityName, WidebandName, AxionName];

    public static SipCodecProfile Resolve(string? name) => name switch
    {
        WidebandName => new(WidebandName, true, AudioCodecsEnum.OPUS, AudioCodecsEnum.G722, AudioCodecsEnum.PCMU, AudioCodecsEnum.PCMA),
        AxionName => new(AxionName, false, AudioCodecsEnum.PCMU),
        _ => new(CompatibilityName, false, AudioCodecsEnum.PCMU, AudioCodecsEnum.PCMA)
    };
}
