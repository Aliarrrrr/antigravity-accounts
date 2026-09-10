using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace AGProfileHub.Core;

public static class DesktopShortcut
{
    public static string FindCliExecutable(string baseDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "AGProfileHub.Cli.exe"),
            Path.Combine(baseDirectory, "..", "cli", "AGProfileHub.Cli.exe"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "AGProfileHub.Cli", "bin", "Release", "net10.0", "AGProfileHub.Cli.exe")
        };
        var found = candidates.FirstOrDefault(File.Exists);
        return found is null
            ? throw new ConfigurationException("CLI executable was not found. Keep the package's hub and cli folders together.", "cli_not_found")
            : PathSafety.CanonicalizeExistingFile(found);
    }

    // Windows argv quoting, including trailing backslashes before the closing quote.
    public static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes);
            result.Append(ch);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [SupportedOSPlatform("windows")]
    public static string Create(ProfileRegistration profile, string dataRoot, string cliExecutable, string directory)
    {
        var executable = PathSafety.CanonicalizeExistingFile(cliExecutable);
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new ConfigurationException("Shortcut destination must be an existing absolute directory.", "shortcut_directory_invalid");
        PathSafety.RejectExistingPathAncestors(directory);
        var name = new string(profile.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).TrimEnd(' ', '.');
        var destination = Path.Combine(directory, $"Antigravity - {name} - {profile.Id:D}.lnk");
        PathSafety.RejectExistingPathAncestors(destination);
        // Save to a unique sibling, then move without overwrite: existing user links are preserved.
        var temporary = Path.Combine(directory, $".agprofile-{Guid.NewGuid():N}.lnk");
        object? link = null;
        try
        {
            try
            {
                link = new ShellLinkComObject();
                var shortcut = (IShellLinkW)link;
                shortcut.SetPath(executable);
                shortcut.SetArguments($"--data-root {QuoteArgument(Path.GetFullPath(dataRoot))} launch {profile.Id:D}");
                shortcut.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
                shortcut.SetDescription($"Launch Antigravity profile: {profile.Name}");
                shortcut.SetIconLocation(executable, 0);
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(temporary, true);
            }
            finally
            {
                if (link is not null)
                {
                    Marshal.FinalReleaseComObject(link);
                    link = null;
                }
            }
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        catch (Exception ex) when (ex is IOException or COMException)
        {
            throw new HubException($"Shortcut could not be created; any existing link was preserved. Windows reported: {ex.Message}", "shortcut_write_failed", ex);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkComObject;

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemIdList);
        void SetIDList(IntPtr itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCommand);
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int capacity, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        void Resolve(IntPtr windowHandle, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
