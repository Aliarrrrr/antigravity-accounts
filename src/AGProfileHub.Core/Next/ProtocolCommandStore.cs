using Microsoft.Win32;

namespace AGProfileHub.Core;

internal interface IProtocolCommandStore
{
    string? Read();
    void Write(string command);
    void Delete();
}

internal sealed class WindowsProtocolCommandStore : IProtocolCommandStore
{
    private const string CommandKey = @"Software\Classes\antigravity\shell\open\command";

    public string? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Registry.CurrentUser.OpenSubKey(CommandKey);
        var value = key?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value switch
        {
            null => null,
            string command => command,
            _ => throw new HubException("The registered callback command has an unsupported value type.", "protocol_owned_elsewhere")
        };
    }

    public void Write(string command)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.CreateSubKey(CommandKey);
        key.SetValue("", command);
    }

    public void Delete()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var key = Registry.CurrentUser.OpenSubKey(CommandKey, writable: true);
        key?.DeleteValue("", throwOnMissingValue: false);
    }
}
