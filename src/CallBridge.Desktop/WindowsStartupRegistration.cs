using Microsoft.Win32;
using System.IO;

namespace CallBridge.Desktop;

public static class WindowsStartupRegistration
{
    public const string DefaultValueName = "CallBridgeDesktop";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Set(bool enabled, string? executablePath = null, string valueName = DefaultValueName)
    {
        ValidateValueName(valueName);
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new IOException("The Windows startup registry key is unavailable.");
        if (enabled)
            key.SetValue(valueName, BuildCommand(executablePath), RegistryValueKind.String);
        else
            key.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public static string BuildCommand(string? executablePath = null)
    {
        var candidate = string.IsNullOrWhiteSpace(executablePath) ? Environment.ProcessPath : executablePath;
        if (string.IsNullOrWhiteSpace(candidate))
            throw new InvalidOperationException("CallBridge could not determine its executable path.");

        var fullPath = Path.GetFullPath(candidate);
        if (!string.Equals(Path.GetFileName(fullPath), "CallBridge.Desktop.exe", StringComparison.OrdinalIgnoreCase)
            || fullPath.Contains('"')
            || fullPath.Any(char.IsControl)
            || !File.Exists(fullPath))
            throw new InvalidOperationException("Windows startup requires the CallBridge.Desktop.exe application file.");

        return $"\"{fullPath}\" --startup";
    }

    public static string? GetRegisteredCommand(string valueName = DefaultValueName)
    {
        ValidateValueName(valueName);
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public static bool IsCurrentExecutableRegistered() =>
        string.Equals(GetRegisteredCommand(), BuildCommand(), StringComparison.Ordinal);

    private static void ValidateValueName(string valueName)
    {
        if (string.IsNullOrWhiteSpace(valueName)
            || valueName.Length > 100
            || valueName.Any(char.IsControl)
            || valueName.Contains('\\'))
            throw new ArgumentException("The Windows startup value name is invalid.", nameof(valueName));
    }
}
