using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CallBridge.Desktop;

public sealed record SupportBundleSnapshot(
    string ApplicationVersion,
    bool ServiceReady,
    bool SipRegistered,
    string Provider,
    string SipTransport,
    string CodecProfile,
    bool SipConfigured,
    bool ConnectWisePsaConfigured,
    bool ConnectWisePlatformConfigured,
    bool LaunchAtStartup,
    bool RegistrationEnabled,
    int MicrophoneCount,
    int SpeakerCount,
    IReadOnlyList<string> StartupLogLines);

public static class SupportBundleBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task CreateAsync(
        string destinationPath,
        SupportBundleSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(snapshot);
        var destination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The support bundle must be a ZIP file.", nameof(destinationPath));

        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                await WriteJsonAsync(archive, "manifest.json", new
                {
                    product = "CallBridge",
                    version = CleanLabel(snapshot.ApplicationVersion),
                    generatedAtUtc = DateTimeOffset.UtcNow,
                    operatingSystem = RuntimeInformation.OSDescription,
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    framework = RuntimeInformation.FrameworkDescription,
                    classification = "sanitized-support-diagnostics"
                }, cancellationToken);
                await WriteJsonAsync(archive, "status.json", new
                {
                    serviceReady = snapshot.ServiceReady,
                    sipRegistered = snapshot.SipRegistered,
                    provider = CleanLabel(snapshot.Provider),
                    sipTransport = CleanLabel(snapshot.SipTransport),
                    codecProfile = CleanLabel(snapshot.CodecProfile),
                    sipConfigured = snapshot.SipConfigured,
                    connectWisePsaConfigured = snapshot.ConnectWisePsaConfigured,
                    connectWisePlatformConfigured = snapshot.ConnectWisePlatformConfigured,
                    launchAtStartup = snapshot.LaunchAtStartup,
                    registrationEnabled = snapshot.RegistrationEnabled,
                    microphoneCount = Math.Clamp(snapshot.MicrophoneCount, 0, 100),
                    speakerCount = Math.Clamp(snapshot.SpeakerCount, 0, 100)
                }, cancellationToken);
                await WriteTextAsync(archive, "startup-events.log", SanitizeStartupEvents(snapshot.StartupLogLines), cancellationToken);
                await WriteTextAsync(
                    archive,
                    "README.txt",
                    "This bundle contains sanitized CallBridge runtime metadata. It excludes credentials, phone numbers, contacts, call history, notes, settings files, databases, network addresses, and audio device names.\r\n",
                    cancellationToken);
                archive.Dispose();
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, destination, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static string SanitizeStartupEvents(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var output = new List<string>();
        foreach (var line in lines.TakeLast(200))
        {
            var separator = line.IndexOf(' ');
            if (separator <= 0 || !DateTimeOffset.TryParse(line[..separator], out var timestamp)) continue;
            var normalized = NormalizeEvent(line[(separator + 1)..]);
            if (normalized is not null) output.Add($"{timestamp.UtcDateTime:O} {normalized}");
        }
        return string.Join(Environment.NewLine, output) + (output.Count == 0 ? "" : Environment.NewLine);
    }

    private static string? NormalizeEvent(string message)
    {
        var categories = new (string Prefix, string Event)[]
        {
            ("Desktop startup entered", "Desktop startup entered."),
            ("Constructing the main window", "Main window construction started."),
            ("Main window constructed", "Main window constructed."),
            ("Main window source initialized", "Main window initialized."),
            ("Main window loaded", "Main window loaded."),
            ("Main window shown", "Main window shown."),
            ("Main window content rendered", "Main window rendered."),
            ("Main window activated", "Main window activated."),
            ("Main window visibility changed", "Main window visibility changed."),
            ("Main window closed", "Main window closed."),
            ("Desktop shutdown complete", "Desktop shutdown completed."),
            ("Desktop application exited", "Desktop application exited."),
            ("Desktop startup failed", "Desktop startup failed; details omitted."),
            ("Local service started", "Local service started."),
            ("The local service exited unexpectedly", "Local service exited unexpectedly."),
            ("Restarting the desktop-owned local service", "Local service restart started."),
            ("Using an externally managed local service", "External local service selected."),
            ("The packaged local service executable was not found", "Packaged local service was unavailable."),
            ("SIP recovery started", "SIP registration recovery started."),
            ("SIP recovery warning", "SIP registration recovery warning."),
            ("Automatic SIP registration did not complete", "Automatic SIP registration did not complete."),
            ("Call history persistence warning", "Call history persistence warning."),
            ("Call history retry warning", "Call history delivery retry warning."),
            ("Recovered from an unreadable settings file", "Unreadable settings were recovered."),
            ("Windows startup registration warning", "Windows startup registration warning."),
            ("Existing CallBridge window activation requested", "Existing window activation requested.")
        };
        foreach (var category in categories)
            if (message.StartsWith(category.Prefix, StringComparison.Ordinal)) return category.Event;
        return null;
    }

    private static string CleanLabel(string value)
    {
        var cleaned = new string((value ?? "").Where(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '/' or '.' or '_').Take(64).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Not available" : cleaned;
    }

    private static async Task WriteJsonAsync(ZipArchive archive, string name, object value, CancellationToken cancellationToken) =>
        await WriteTextAsync(archive, name, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

    private static async Task WriteTextAsync(ZipArchive archive, string name, string value, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(value.AsMemory(), cancellationToken);
    }
}
