using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Windows;

var audio = new WindowsAudioEndPoint(new AudioEncoder());
var media = new RTPSession(false, false, false) { AcceptRtpFromAny = true };
media.addTrack(new MediaStreamTrack(audio.GetAudioSourceFormats(), MediaStreamStatusEnum.SendRecv));
audio.OnAudioSourceEncodedSample += media.SendAudio;
media.Close("smoke test");
await audio.CloseAudio();
await audio.CloseAudioSink();
Console.WriteLine("SIP Windows audio/media session initialized successfully.");
