using System;
using System.Text.Json;
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
Console.WriteLine("Credential protection round-trip passed; plaintext is absent from stored value.");
return 0;
