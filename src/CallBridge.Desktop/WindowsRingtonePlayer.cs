using NAudio.Wave;

namespace CallBridge.Desktop;

public sealed class WindowsRingtonePlayer : IDisposable
{
    private readonly object _sync = new();
    private WaveOut? _output;

    public event Action<string>? PlaybackError;

    public bool IsPlaying
    {
        get
        {
            lock (_sync) return _output?.PlaybackState == PlaybackState.Playing;
        }
    }

    public void Start(string? selectedDevice)
    {
        if (Environment.GetEnvironmentVariable("CALLBRIDGE_DISABLE_AUDIO_PLAYBACK") == "1") return;
        lock (_sync)
        {
            StopCore();
            try
            {
                var resolution = WindowsAudioDeviceCatalog.ResolveSpeaker(selectedDevice);
                if (resolution.UserMessage is not null) PlaybackError?.Invoke(resolution.UserMessage);
                _output = new WaveOut { DeviceNumber = resolution.Device.DeviceIndex };
                _output.Init(new RingtoneWaveProvider());
                _output.Play();
            }
            catch
            {
                StopCore();
                PlaybackError?.Invoke("The ringtone speaker could not be opened. Choose another ringtone speaker in Settings.");
            }
        }
    }

    public void Stop()
    {
        lock (_sync) StopCore();
    }

    private void StopCore()
    {
        if (_output is null) return;
        try { _output.Stop(); } catch { }
        _output.Dispose();
        _output = null;
    }

    public void Dispose() => Stop();

    private sealed class RingtoneWaveProvider : IWaveProvider
    {
        private const int SampleRate = 16000;
        private long _samplePosition;

        public WaveFormat WaveFormat { get; } = new(SampleRate, 16, 1);

        public int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public int Read(Span<byte> buffer)
        {
            var sampleCount = buffer.Length / sizeof(short);
            for (var index = 0; index < sampleCount; index++, _samplePosition++)
            {
                var cadencePosition = _samplePosition % (SampleRate * 3L);
                var active = cadencePosition < SampleRate;
                var fadeSamples = Math.Min(cadencePosition, SampleRate - cadencePosition);
                var envelope = active ? Math.Clamp(fadeSamples / 320d, 0d, 1d) : 0d;
                var time = _samplePosition / (double)SampleRate;
                var sample = active
                    ? (short)((Math.Sin(2d * Math.PI * 440d * time) + Math.Sin(2d * Math.PI * 480d * time)) * short.MaxValue * 0.07d * envelope)
                    : (short)0;
                BitConverter.TryWriteBytes(buffer.Slice(index * sizeof(short), sizeof(short)), sample);
            }

            if ((buffer.Length & 1) != 0) buffer[^1] = 0;
            return buffer.Length;
        }
    }
}
