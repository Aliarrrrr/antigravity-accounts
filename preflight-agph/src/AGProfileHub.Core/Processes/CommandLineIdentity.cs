using System.Runtime.InteropServices;

namespace AGProfileHub.Core;

public static class CommandLineIdentity
{
    public static string? TryGetUserDataPath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var arguments = Split(commandLine);
        var found = new List<string>();
        for (var i = 1; i < arguments.Count; i++)
        {
            const string prefix = "--user-data-dir=";
            if (arguments[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) found.Add(arguments[i][prefix.Length..]);
            else if (arguments[i].Equals("--user-data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count) found.Add(arguments[++i]);
        }
        return found.Count == 1 ? Normalize(found[0]) : null;
    }

    private static string? Normalize(string candidate)
    {
        try { return Path.GetFullPath(candidate); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    internal static IReadOnlyList<string> Split(string commandLine)
    {
        if (OperatingSystem.IsWindows())
        {
            var pointer = CommandLineToArgvW(commandLine, out var count);
            if (pointer == 0) return [];
            try
            {
                var result = new string[count];
                for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size)) ?? string.Empty;
                return result;
            }
            finally { LocalFree(pointer); }
        }
        return SplitWindowsStyle(commandLine);
    }

    private static IReadOnlyList<string> SplitWindowsStyle(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var slashes = 0;
        foreach (var character in commandLine)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                current.Append('\\', slashes / 2);
                if ((slashes & 1) != 0) current.Append('"');
                else quoted = !quoted;
                slashes = 0;
                continue;
            }
            current.Append('\\', slashes);
            slashes = 0;
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(character);
        }
        current.Append('\\', slashes);
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern nint CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argc);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
