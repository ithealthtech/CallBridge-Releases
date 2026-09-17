using System.Reflection;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Windows;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

Console.WriteLine("DELEGATE " + typeof(EncodedSampleDelegate).GetMethod("Invoke"));
foreach (var p in typeof(EncodedAudioFrame).GetProperties()) Console.WriteLine($"FRAME {p.PropertyType} {p.Name}");
foreach (var c in typeof(EncodedAudioFrame).GetConstructors()) Console.WriteLine($"FRAMECTOR {c}");

foreach (var type in new[] { typeof(RTPSession), typeof(MediaStreamTrack), typeof(WindowsAudioEndPoint) })
{
    Console.WriteLine($"TYPE {type.FullName}");
    Console.WriteLine("INTERFACES " + string.Join(",", type.GetInterfaces().Select(x => x.FullName)));
    foreach (var constructor in type.GetConstructors()) Console.WriteLine(constructor);
    foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).Where(m => m.Name.Contains("Audio") || m.Name.Contains("Rtp") || m.Name.Contains("Track") || m.Name.Contains("Format") || m.Name is "Start" or "Stop" or "Close")) Console.WriteLine(method);
    foreach (var evt in type.GetEvents()) Console.WriteLine($"EVENT {evt.Name}: {evt.EventHandlerType}");
}

foreach (var method in typeof(WindowsAudioEndPoint).GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.Name.Contains("EncodedMedia", StringComparison.Ordinal))) Console.WriteLine($"ENCODED {method}");

foreach (var type in typeof(SIPTransport).Assembly.GetTypes()
    .Where(type => type.Namespace == "SIPSorcery.SIP" && (type.Name.Contains("Channel", StringComparison.Ordinal) || type.Name == nameof(SIPTransport)))
    .OrderBy(type => type.Name))
{
    Console.WriteLine($"SIPTYPE {type.FullName}");
    foreach (var constructor in type.GetConstructors()) Console.WriteLine($"SIPCTOR {constructor}");
}

foreach (var property in typeof(SIPURI).GetProperties(BindingFlags.Instance | BindingFlags.Public))
    Console.WriteLine($"SIPURIPROP {property.PropertyType} {property.Name} writable={property.CanWrite}");
foreach (var field in typeof(SIPURI).GetFields(BindingFlags.Instance | BindingFlags.Public))
    Console.WriteLine($"SIPURIFIELD {field.FieldType} {field.Name}");
foreach (var constructor in typeof(SIPURI).GetConstructors()) Console.WriteLine($"SIPURICTOR {constructor}");

var defaultAudio = new WindowsAudioEndPoint(new SIPSorcery.Media.AudioEncoder());
Console.WriteLine("CODECS DEFAULT " + string.Join(",", defaultAudio.GetAudioSourceFormats().Select(format => format.Codec)));
await defaultAudio.CloseAudio();
await defaultAudio.CloseAudioSink();
var opusAudio = new WindowsAudioEndPoint(new SIPSorcery.Media.AudioEncoder(includeOpus: true));
Console.WriteLine("CODECS OPUS " + string.Join(",", opusAudio.GetAudioSourceFormats().Select(format => format.Codec)));
await opusAudio.CloseAudio();
await opusAudio.CloseAudioSink();

foreach (var method in typeof(SIPSorcery.SIP.App.SIPUserAgent).GetMethods(BindingFlags.Instance | BindingFlags.Public)
    .Where(method => method.Name.Contains("Transfer", StringComparison.OrdinalIgnoreCase)
        || method.Name.Contains("Progress", StringComparison.OrdinalIgnoreCase)
        || method.Name.Contains("Call", StringComparison.OrdinalIgnoreCase)))
    Console.WriteLine($"UAMETHOD {method}");
foreach (var eventInfo in typeof(SIPSorcery.SIP.App.SIPUserAgent).GetEvents())
    Console.WriteLine($"UAEVENT {eventInfo.Name}: {eventInfo.EventHandlerType}");
foreach (var property in typeof(SIPSorcery.SIP.App.SIPUserAgent).GetProperties(BindingFlags.Instance | BindingFlags.Public))
    Console.WriteLine($"UAPROP {property.PropertyType} {property.Name} writable={property.CanWrite}");

foreach (var type in new[]
{
    typeof(RTCPCompoundPacket),
    typeof(RTCPReceiverReport),
    typeof(RTCPSenderReport),
    typeof(ReceptionReportSample),
    typeof(RTPHeader),
    typeof(AudioFormat)
})
{
    Console.WriteLine($"QUALITYTYPE {type.FullName}");
    foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        Console.WriteLine($"QUALITYPROP {property.PropertyType} {property.Name} writable={property.CanWrite}");
    foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
        Console.WriteLine($"QUALITYFIELD {field.FieldType} {field.Name}");
}
