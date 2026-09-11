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
            Path.Combine(baseDirectory, "..", "..", "..", "..", "AGProfileHub.Cli", "bin", "Release", "net10.0", "AGProfileHub.Cli.exe"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "AGProfileHub.Cli", "bin", "Debug", "net10.0", "AGProfileHub.Cli.exe")
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
        object? shell = null;
        object? link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
            link = ((dynamic)shell!).CreateShortcut(temporary);
            dynamic shortcut = link;
            shortcut.TargetPath = executable;
            shortcut.Arguments = $"--data-root {QuoteArgument(Path.GetFullPath(dataRoot))} launch {profile.Id:D}";
            shortcut.WorkingDirectory = Path.GetDirectoryName(executable)!;
            shortcut.Description = $"Launch Antigravity profile: {profile.Name}";
            shortcut.IconLocation = executable + ",0";
            shortcut.Save();
            File.Move(temporary, destination, overwrite: false);
            return destination;
        }
        catch (IOException ex)
        {
            throw new HubException("Shortcut could not be created; any existing link was preserved. Choose another directory or remove the old link yourself.", "shortcut_write_failed", ex);
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
