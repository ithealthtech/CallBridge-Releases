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
