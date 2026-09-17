using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CallBridge.Desktop;

public sealed record AppSettingsLoadResult(AppSettings Settings, bool CanSave, bool RecoveredCorruptFile);

public static class AppSettingsStore
{
    private const int MaximumSettingsBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppSettingsLoadResult Load(string path, string? legacyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var primaryPath = Path.GetFullPath(path);
        var sourcePath = File.Exists(primaryPath)
            ? primaryPath
            : !string.IsNullOrWhiteSpace(legacyPath) && File.Exists(legacyPath)
                ? Path.GetFullPath(legacyPath)
                : null;

        if (sourcePath is null) return new(new AppSettings(), true, false);

        try
        {
            var info = new FileInfo(sourcePath);
            if (info.Length > MaximumSettingsBytes) throw new InvalidDataException("The settings file is too large.");
            var json = File.ReadAllText(sourcePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                ?? throw new InvalidDataException("The settings file is empty.");
            HydrateSecrets(settings, json);
            return new(settings, true, false);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException
            or FormatException
            or CryptographicException
            or System.ComponentModel.Win32Exception)
        {
            if (!string.Equals(sourcePath, primaryPath, StringComparison.OrdinalIgnoreCase))
                return new(new AppSettings(), true, true);

            var quarantined = TryQuarantine(primaryPath);
            return new(new AppSettings(), quarantined, quarantined);
        }
    }

    public static void Save(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);

        settings.SipPasswordProtected = ProtectOptional(settings.SipPassword);
        settings.ConnectWisePrivateKeyProtected = ProtectOptional(settings.ConnectWisePrivateKey);
        settings.ConnectWisePlatformClientSecretProtected = ProtectOptional(settings.ConnectWisePlatformClientSecret);
        settings.ConnectWisePlatformAccessTokenProtected = ProtectOptional(settings.ConnectWisePlatformAccessToken);

        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(json);
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void HydrateSecrets(AppSettings settings, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        settings.SipPassword = UnprotectOrMigrate(settings.SipPasswordProtected, root, "sipPassword");
        settings.ConnectWisePrivateKey = UnprotectOrMigrate(settings.ConnectWisePrivateKeyProtected, root, "connectWisePrivateKey");
        settings.ConnectWisePlatformClientSecret = UnprotectOrMigrate(settings.ConnectWisePlatformClientSecretProtected, root, "connectWisePlatformClientSecret");
        settings.ConnectWisePlatformAccessToken = UnprotectOrMigrate(settings.ConnectWisePlatformAccessTokenProtected, root, "connectWisePlatformAccessToken");
    }

    private static string UnprotectOrMigrate(string protectedValue, JsonElement root, string legacyName)
    {
        if (!string.IsNullOrWhiteSpace(protectedValue)) return CredentialProtector.Unprotect(protectedValue);
        return root.TryGetProperty(legacyName, out var legacyValue) && legacyValue.ValueKind == JsonValueKind.String
            ? legacyValue.GetString() ?? ""
            : "";
    }

    private static string ProtectOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : CredentialProtector.Protect(value);

    private static bool TryQuarantine(string path)
    {
        try
        {
            var raw = File.ReadAllText(path);
            var protectedRaw = CredentialProtector.Protect(raw);
            var quarantinePath = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.protected";
            var temporaryPath = $"{quarantinePath}.tmp";
            try
            {
                File.WriteAllText(temporaryPath, protectedRaw, new UTF8Encoding(false));
                File.Move(temporaryPath, quarantinePath);
                File.Delete(path);
                return !File.Exists(path);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or CryptographicException
            or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
