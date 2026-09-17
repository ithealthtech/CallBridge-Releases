using NAudio.Wave;

namespace CallBridge.Desktop;

public sealed record AudioDeviceTestResult(bool Success, string Message);

public static class WindowsAudioDeviceTester
{
    public static async Task<AudioDeviceTestResult> TestMicrophoneAsync(string? selectedDevice, CancellationToken cancellationToken = default)
    {
        var resolution = WindowsAudioDeviceCatalog.ResolveMicrophone(selectedDevice);
        var peak = 0;
        try
        {
            using var input = new WaveIn
            {
                DeviceNumber = resolution.Device.DeviceIndex,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };
            input.DataAvailable += (_, args) =>
            {
                for (var offset = 0; offset + 1 < args.BytesRecorded; offset += 2)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(args.Buffer, offset)));
            };
            input.StartRecording();
            await Task.Delay(TimeSpan.FromMilliseconds(1400), cancellationToken);
            input.StopRecording();
        }
        catch (OperationCanceledException)
        {
            return new(false, "Microphone test cancelled.");
        }
        catch
        {
            return new(false, "The microphone could not be opened. Reconnect it or choose another microphone.");
        }

        return peak >= 100
            ? new(true, "Microphone is working and receiving sound.")
            : new(false, "The microphone opened, but no sound was detected. Check its mute and input level.");
    }

    public static async Task<AudioDeviceTestResult> TestSpeakerAsync(string? selectedDevice, CancellationToken cancellationToken = default)
    {
        var resolution = WindowsAudioDeviceCatalog.ResolveSpeaker(selectedDevice);
        const int sampleRate = 16000;
        const int durationMilliseconds = 650;
        var sampleCount = sampleRate * durationMilliseconds / 1000;
        var samples = new byte[sampleCount * sizeof(short)];
        for (var index = 0; index < sampleCount; index++)
        {
            var envelope = Math.Min(1d, Math.Min(index / 400d, (sampleCount - index) / 400d));
            var value = (short)(Math.Sin(2d * Math.PI * 523.25d * index / sampleRate) * short.MaxValue * 0.12d * envelope);
            BitConverter.TryWriteBytes(samples.AsSpan(index * sizeof(short), sizeof(short)), value);
        }

        try
        {
            var provider = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1));
            provider.AddSamples(samples, 0, samples.Length);
            using var output = new WaveOut { DeviceNumber = resolution.Device.DeviceIndex };
            output.Init(provider);
            output.Play();
            await Task.Delay(TimeSpan.FromMilliseconds(durationMilliseconds + 150), cancellationToken);
            output.Stop();
            return new(true, "Speaker test played. Confirm that you heard the tone.");
        }
        catch (OperationCanceledException)
        {
            return new(false, "Speaker test cancelled.");
        }
        catch
        {
            return new(false, "The speaker could not be opened. Reconnect it or choose another speaker.");
        }
    }
}
