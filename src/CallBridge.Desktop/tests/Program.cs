using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CallBridge.Desktop;

const string value = "runtime-secret-test";
var encrypted = CredentialProtector.Protect(value);
var decrypted = CredentialProtector.Unprotect(encrypted);
if (encrypted.Contains(value, StringComparison.Ordinal) || decrypted != value) return 1;
var serialized = JsonSerializer.Serialize(new AppSettings
{
    ConnectWisePrivateKey = value,
    ConnectWisePrivateKeyProtected = encrypted,
    ConnectWisePlatformClientSecret = value,
    ConnectWisePlatformClientSecretProtected = encrypted,
    ConnectWisePlatformAccessToken = value,
    ConnectWisePlatformAccessTokenProtected = encrypted
});
var roundTrip = JsonSerializer.Deserialize<AppSettings>(serialized);
if (serialized.Contains(value, StringComparison.Ordinal)
    || roundTrip?.ConnectWisePrivateKeyProtected != encrypted
    || roundTrip.ConnectWisePlatformClientSecretProtected != encrypted
    || roundTrip.ConnectWisePlatformAccessTokenProtected != encrypted
    || !string.IsNullOrEmpty(roundTrip.ConnectWisePrivateKey)
    || !string.IsNullOrEmpty(roundTrip.ConnectWisePlatformClientSecret)
    || !string.IsNullOrEmpty(roundTrip.ConnectWisePlatformAccessToken)) return 2;

var testRoot = Path.Combine(Path.GetTempPath(), $"callbridge-queue-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);
try
{
    var queuePath = Path.Combine(testRoot, "call-events.queue");
    var queue = new ProtectedCallEventQueue(queuePath, 3);
    var first = new SipCallLifecycleEvent("private-call-one", SipCallDirection.Outbound, "1555010101", SipCallLifecycleState.Started, DateTimeOffset.UtcNow, "event-one");
    var second = new SipCallLifecycleEvent("private-call-two", SipCallDirection.Inbound, "1555010102", SipCallLifecycleState.Ringing, DateTimeOffset.UtcNow.AddSeconds(1), "event-two");

    await queue.EnqueueAndDrainAsync(first, _ => Task.FromResult(false));
    await queue.EnqueueAndDrainAsync(first, _ => Task.FromResult(false));
    await queue.EnqueueAndDrainAsync(second, _ => Task.FromResult(false));
    if (await queue.CountAsync() != 2 || !File.Exists(queuePath)) return 3;

    var protectedQueue = await File.ReadAllTextAsync(queuePath);
    if (protectedQueue.Contains(first.CallId, StringComparison.Ordinal)
        || protectedQueue.Contains(first.RemoteNumber, StringComparison.Ordinal)
        || protectedQueue.Contains(first.EventId, StringComparison.Ordinal)) return 4;

    var delivered = new List<SipCallLifecycleEvent>();
    var reloadedQueue = new ProtectedCallEventQueue(queuePath, 3);
    await reloadedQueue.DrainAsync(callEvent =>
    {
        delivered.Add(callEvent);
        return Task.FromResult(true);
    });
    if (delivered.Count != 2
        || delivered[0].EventId != first.EventId
        || delivered[1].EventId != second.EventId
        || await reloadedQueue.CountAsync() != 0
        || File.Exists(queuePath)) return 5;

    await File.WriteAllTextAsync(queuePath, CredentialProtector.Protect("{not-valid-json"));
    var corruptQueue = new ProtectedCallEventQueue(queuePath);
    if (await corruptQueue.CountAsync() != 0
        || File.Exists(queuePath)
        || Directory.GetFiles(testRoot, "call-events.queue.corrupt-*").Length != 1) return 6;

    var settingsPath = Path.Combine(testRoot, "settings.json");
    var settingsSecret = "settings-secret-marker";
    var settings = new AppSettings
    {
        SipPassword = settingsSecret,
        ConnectWisePrivateKey = settingsSecret,
        ConnectWisePlatformClientSecret = settingsSecret,
        ConnectWisePlatformAccessToken = settingsSecret,
        RegistrationEnabled = false
    };
    AppSettingsStore.Save(settingsPath, settings);
    var storedSettings = await File.ReadAllTextAsync(settingsPath);
    if (storedSettings.Contains(settingsSecret, StringComparison.Ordinal)
        || Directory.GetFiles(testRoot, "settings.json.*.tmp").Length != 0) return 7;

    var loadedSettings = AppSettingsStore.Load(settingsPath);
    if (!loadedSettings.CanSave
        || loadedSettings.RecoveredCorruptFile
        || loadedSettings.Settings.SipPassword != settingsSecret
        || loadedSettings.Settings.ConnectWisePrivateKey != settingsSecret
        || loadedSettings.Settings.ConnectWisePlatformClientSecret != settingsSecret
        || loadedSettings.Settings.ConnectWisePlatformAccessToken != settingsSecret
        || loadedSettings.Settings.RegistrationEnabled) return 8;

    const string corruptSecret = "corrupt-plaintext-secret";
    await File.WriteAllTextAsync(settingsPath, $"{{invalid-json-{corruptSecret}");
    var recoveredSettings = AppSettingsStore.Load(settingsPath);
    var quarantineFiles = Directory.GetFiles(testRoot, "settings.json.corrupt-*.protected");
    if (!recoveredSettings.CanSave
        || !recoveredSettings.RecoveredCorruptFile
        || File.Exists(settingsPath)
        || quarantineFiles.Length != 1) return 9;
    var protectedCorruptSettings = await File.ReadAllTextAsync(quarantineFiles[0]);
    if (protectedCorruptSettings.Contains(corruptSecret, StringComparison.Ordinal)
        || !CredentialProtector.Unprotect(protectedCorruptSettings).Contains(corruptSecret, StringComparison.Ordinal)) return 10;

    const string legacySecret = "legacy-plaintext-secret";
    await File.WriteAllTextAsync(settingsPath, $$"""{"sipPassword":"{{legacySecret}}","connectWisePrivateKey":"{{legacySecret}}"}""");
    var legacySettings = AppSettingsStore.Load(settingsPath);
    if (legacySettings.Settings.SipPassword != legacySecret
        || legacySettings.Settings.ConnectWisePrivateKey != legacySecret) return 11;
    AppSettingsStore.Save(settingsPath, legacySettings.Settings);
    if ((await File.ReadAllTextAsync(settingsPath)).Contains(legacySecret, StringComparison.Ordinal)) return 12;

    var startupDirectory = Path.Combine(testRoot, "startup path with spaces");
    Directory.CreateDirectory(startupDirectory);
    var startupExecutable = Path.Combine(startupDirectory, "CallBridge.Desktop.exe");
    await File.WriteAllBytesAsync(startupExecutable, [0x4d, 0x5a]);
    var startupValueName = $"CallBridgeDesktop.Test.{Guid.NewGuid():N}";
    var expectedStartupCommand = $"\"{Path.GetFullPath(startupExecutable)}\" --startup";
    if (WindowsStartupRegistration.BuildCommand(startupExecutable) != expectedStartupCommand) return 13;
    try
    {
        WindowsStartupRegistration.Set(true, startupExecutable, startupValueName);
        if (WindowsStartupRegistration.GetRegisteredCommand(startupValueName) != expectedStartupCommand) return 14;
    }
    finally
    {
        WindowsStartupRegistration.Set(false, startupExecutable, startupValueName);
    }
    if (WindowsStartupRegistration.GetRegisteredCommand(startupValueName) is not null) return 15;
    try
    {
        WindowsStartupRegistration.BuildCommand(Path.Combine(startupDirectory, "NotCallBridge.exe"));
        return 16;
    }
    catch (InvalidOperationException)
    {
    }

    var bundlePath = Path.Combine(testRoot, "support.zip");
    var leakedSecret = "support-secret-marker";
    var leakedPhone = "15551234567";
    var leakedEmail = "person@example.test";
    var leakedPath = @"C:\Users\PrivatePerson\settings.json";
    await SupportBundleBuilder.CreateAsync(bundlePath, new SupportBundleSnapshot(
        "0.13.0",
        true,
        false,
        "Standard SIP",
        "TLS",
        "Compatibility",
        true,
        true,
        false,
        true,
        true,
        2,
        3,
        [
            $"2026-09-02T12:00:00Z Desktop startup failed: bearer {leakedSecret} {leakedPhone} {leakedEmail} {leakedPath}",
            $"2026-09-02T12:00:01Z unknown event {leakedSecret}",
            "2026-09-02T12:00:02Z Local service started (process 12345)."
        ]));
    using (var bundle = ZipFile.OpenRead(bundlePath))
    {
        var names = bundle.Entries.Select(entry => entry.FullName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (!names.SequenceEqual(new[] { "README.txt", "manifest.json", "startup-events.log", "status.json" })) return 17;
        var contents = new StringBuilder();
        foreach (var entry in bundle.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            contents.Append(await reader.ReadToEndAsync());
        }
        var text = contents.ToString();
        if (text.Contains(leakedSecret, StringComparison.Ordinal)
            || text.Contains(leakedPhone, StringComparison.Ordinal)
            || text.Contains(leakedEmail, StringComparison.Ordinal)
            || text.Contains(leakedPath, StringComparison.Ordinal)
            || !text.Contains("Desktop startup failed; details omitted.", StringComparison.Ordinal)
            || !text.Contains("Local service started.", StringComparison.Ordinal)) return 18;
    }
}
finally
{
    Directory.Delete(testRoot, true);
}

Console.WriteLine("Credential, settings recovery, startup registration, encrypted queue, and sanitized support bundle tests passed.");
return 0;
