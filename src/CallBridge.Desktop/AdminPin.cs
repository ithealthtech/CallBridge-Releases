using System.Security.Cryptography;
using System.Text;

namespace CallBridge.Desktop;

/// <summary>
/// Salted PBKDF2 hashing for the admin PIN that gates admin settings. The PIN keeps technicians
/// out of provider and integration settings in the UI; it is not a boundary against someone who
/// can edit this Windows user's settings file.
/// </summary>
public static class AdminPin
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static bool IsValidFormat(string? pin) =>
        pin is { Length: >= 4 and <= 12 } && pin.All(char.IsAsciiDigit);

    public static (string Hash, string Salt) Create(string pin)
    {
        if (!IsValidFormat(pin)) throw new ArgumentException("The admin PIN must be 4 to 12 digits.", nameof(pin));
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return (Convert.ToBase64String(Derive(pin, salt)), Convert.ToBase64String(salt));
    }

    public static bool Verify(string? pin, string? hash, string? salt)
    {
        if (!IsValidFormat(pin) || string.IsNullOrWhiteSpace(hash) || string.IsNullOrWhiteSpace(salt)) return false;
        try
        {
            var expected = Convert.FromBase64String(hash);
            var actual = Derive(pin!, Convert.FromBase64String(salt));
            return expected.Length == HashBytes && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Delay before another attempt is allowed after repeated failures: none for the first four, then growing by 30 seconds.</summary>
    public static TimeSpan RetryDelay(int failedAttempts) =>
        failedAttempts < 5 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(300, 30 * (failedAttempts - 4)));

    private static byte[] Derive(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
}
