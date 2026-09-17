using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CallBridge.Desktop;

/// <summary>A newer CallBridge installer published on the public releases repository.</summary>
public sealed record UpdateInfo(Version Version, string Tag, Uri InstallerUri, string Sha256, Uri ReleasePage);

/// <summary>
/// Checks the public releases repository (installers only; source stays private) for a newer MSI,
/// downloads it, verifies the SHA256 published in the release notes, and hands it to Windows Installer.
/// </summary>
public static partial class UpdateChecker
{
    public const string ReleasesRepository = "ithealthtech/CallBridge-Releases";
    private static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{ReleasesRepository}/releases/latest");
    private const long MaxInstallerBytes = 512L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion
    {
        get
        {
            var informational = typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return TryParseVersion(informational, out var version) ? version : new Version(0, 0, 0);
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return ParseRelease(document.RootElement, CurrentVersion);
    }

    /// <summary>Pure parsing of a GitHub "latest release" payload; returns null unless it is a newer, well-formed release.</summary>
    public static UpdateInfo? ParseRelease(JsonElement release, Version current)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (!TryParseVersion(tag, out var version) || version <= current) return null;

        var body = release.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() ?? "" : "";
        var hash = Sha256Pattern().Match(body);
        if (!hash.Success) return null;

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        Uri? installer = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is null || url is null || !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) continue;
            if (Uri.TryCreate(url, UriKind.Absolute, out var candidate)
                && candidate.Scheme == Uri.UriSchemeHttps
                && candidate.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && candidate.AbsolutePath.StartsWith($"/{ReleasesRepository}/releases/download/", StringComparison.OrdinalIgnoreCase))
            {
                installer = candidate;
                break;
            }
        }
        if (installer is null) return null;

        var page = release.TryGetProperty("html_url", out var html) && Uri.TryCreate(html.GetString(), UriKind.Absolute, out var htmlUri)
            && htmlUri.Scheme == Uri.UriSchemeHttps && htmlUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? htmlUri
            : new Uri($"https://github.com/{ReleasesRepository}/releases");
        return new UpdateInfo(version, tag!, installer, hash.Groups[1].Value.ToUpperInvariant(), page);
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = VersionPattern().Match(text.Trim());
        if (!match.Success) return false;
        version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value));
        return true;
    }

    /// <summary>Downloads the installer to a private temp folder and returns its path only if the SHA256 matches.</summary>
    public static async Task<string> DownloadVerifiedAsync(UpdateInfo update, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "CallBridge-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"CallBridge-v{update.Version}.msi");
        var partial = path + ".partial";

        using (var response = await Http.GetAsync(update.InstallerUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total > MaxInstallerBytes) throw new InvalidDataException("The update is larger than expected.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                written += read;
                if (written > MaxInstallerBytes) throw new InvalidDataException("The update is larger than expected.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                if (total > 0) progress?.Report((int)(written * 100 / total.Value));
            }
        }

        string actual;
        await using (var file = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
        if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException("The downloaded update didn't match its published checksum, so it wasn't installed.");
        }
        File.Move(partial, path, true);
        return path;
    }

    /// <summary>Starts Windows Installer for the verified MSI (Windows shows the admin prompt).</summary>
    public static void LaunchInstaller(string msiPath)
    {
        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        Process.Start(new ProcessStartInfo(msiexec)
        {
            Arguments = $"/i \"{msiPath}\" /passive /norestart",
            UseShellExecute = true,
            Verb = "runas"
        });
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"CallBridge/{CurrentVersion}");
        return client;
    }

    [GeneratedRegex(@"^v?(\d{1,5})\.(\d{1,5})\.(\d{1,6})(?:[+-].*)?$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"SHA256:\s*([0-9A-Fa-f]{64})\b")]
    private static partial Regex Sha256Pattern();
}
