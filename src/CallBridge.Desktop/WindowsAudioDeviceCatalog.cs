using System.Security.Cryptography;
using System.Text;
using NAudio.Wave;

namespace CallBridge.Desktop;

public enum WindowsAudioDeviceKind
{
    Microphone,
    Speaker
}

public sealed record WindowsAudioDeviceOption(
    string Id,
    string DisplayName,
    int DeviceIndex,
    bool IsDefault)
{
    public override string ToString() => DisplayName;
}

public sealed record WindowsAudioDeviceResolution(
    WindowsAudioDeviceOption Device,
    bool FellBack,
    string? UserMessage);

public static class WindowsAudioDeviceCatalog
{
    public const string DefaultDeviceId = "windows:default";
    public const string DefaultDeviceName = "Windows default device";
    public const string LegacyDefaultDeviceName = "Windows default communications device";

    public static IReadOnlyList<WindowsAudioDeviceOption> GetMicrophones() => Enumerate(WindowsAudioDeviceKind.Microphone);

    public static IReadOnlyList<WindowsAudioDeviceOption> GetSpeakers() => Enumerate(WindowsAudioDeviceKind.Speaker);

    public static WindowsAudioDeviceResolution ResolveMicrophone(string? selectedDevice) =>
        Resolve(GetMicrophones(), selectedDevice, "microphone");

    public static WindowsAudioDeviceResolution ResolveSpeaker(string? selectedDevice) =>
        Resolve(GetSpeakers(), selectedDevice, "speaker");

    public static WindowsAudioDeviceResolution Resolve(
        IReadOnlyList<WindowsAudioDeviceOption> devices,
        string? selectedDevice,
        string deviceLabel)
    {
        var defaultDevice = devices.FirstOrDefault(device => device.IsDefault)
            ?? new WindowsAudioDeviceOption(DefaultDeviceId, DefaultDeviceName, -1, true);
        if (string.IsNullOrWhiteSpace(selectedDevice)
            || string.Equals(selectedDevice, DefaultDeviceId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedDevice, DefaultDeviceName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(selectedDevice, LegacyDefaultDeviceName, StringComparison.OrdinalIgnoreCase))
        {
            return new(defaultDevice, false, null);
        }

        var selected = devices.FirstOrDefault(device =>
            string.Equals(device.Id, selectedDevice, StringComparison.OrdinalIgnoreCase)
            || string.Equals(device.DisplayName, selectedDevice, StringComparison.OrdinalIgnoreCase));
        return selected is not null
            ? new(selected, false, null)
            : new(defaultDevice, true, $"The selected {deviceLabel} is unavailable. CallBridge will use the Windows default device.");
    }

    private static IReadOnlyList<WindowsAudioDeviceOption> Enumerate(WindowsAudioDeviceKind kind)
    {
        var devices = new List<WindowsAudioDeviceOption>
        {
            new(DefaultDeviceId, DefaultDeviceName, -1, true)
        };

        try
        {
            var count = kind == WindowsAudioDeviceKind.Microphone ? WaveIn.DeviceCount : WaveOut.DeviceCount;
            var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < count; index++)
            {
                var productName = kind == WindowsAudioDeviceKind.Microphone
                    ? WaveIn.GetCapabilities(index).ProductName
                    : WaveOut.GetCapabilities(index).ProductName;
                var name = string.IsNullOrWhiteSpace(productName) ? $"Audio device {index + 1}" : productName.Trim();
                occurrences.TryGetValue(name, out var occurrence);
                occurrence++;
                occurrences[name] = occurrence;
                var displayName = occurrence == 1 ? name : $"{name} ({occurrence})";
                devices.Add(new(CreateStableId(kind, name, occurrence), displayName, index, false));
            }
        }
        catch
        {
            // The Windows default remains usable when a driver refuses enumeration.
        }

        return devices;
    }

    private static string CreateStableId(WindowsAudioDeviceKind kind, string name, int occurrence)
    {
        var identity = $"{kind}|{name.ToUpperInvariant()}|{occurrence}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var direction = kind == WindowsAudioDeviceKind.Microphone ? "input" : "output";
        return $"windows:{direction}:{Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant()}";
    }
}
