using System.Diagnostics;
using System.Text;

namespace AGProfileHub.Core;

public sealed class NextBridge(HubPaths paths)
{
    private string Manifest(Guid id) => Path.Combine(paths.ProfileRoot(id), "bridge.json");
    private AtomicJsonStore Store => new(paths.DataRoot);
    public Task<BridgeDocument> ReadAsync(Guid id, CancellationToken token = default) =>
        Store.ReadAsync(Manifest(id), NextJsonContext.Default.BridgeDocument, () => new BridgeDocument(), token);

    private static string GitInclude(string target) => "# Managed by AntigravityProfileHubNext\n[include]\n\tpath = \"" +
        target.Replace("\\", "/").Replace("\"", "\\\"") + "\"\n";

    public async Task<BridgeDocument> ApplyAsync(Guid id, bool enabled, string hostHome, CancellationToken token = default)
    {
        await using var held = await FileLock.AcquireAsync(Path.Combine(paths.LocksRoot, "bridge-" + id.ToString("D") + ".lock"), TimeSpan.FromSeconds(30), token);
        hostHome = Path.GetFullPath(hostHome);
        if (hostHome.Contains('"') || hostHome.Any(char.IsControl)) throw new ConfigurationException("Invalid bridge target.");
        PathSafety.RejectExistingPathAncestors(hostHome);
        paths.EnsureProfileLayout(id);
        var document = await ReadAsync(id, token);
        if (document.ManagedLinks.Keys.Any(name => name is not ".gitconfig" and not ".ssh"))
            throw new ConfigurationException("Unknown managed bridge item; refused.");
        var managed = new Dictionary<string, string>(document.ManagedLinks);
        var ownershipConflicts = new List<string>();
        foreach (var item in document.ManagedLinks)
        {
            var path = Path.Combine(paths.Home(id), item.Key);
            var desired = Path.Combine(hostHome, item.Key);
            if (enabled && SamePath(item.Value, desired)) continue;
            try { RemoveManaged(path, item.Key, item.Value); }
            catch (HubException ex) when (ex.Code == "bridge_ownership_changed") { ownershipConflicts.Add(item.Key); continue; }
            managed.Remove(item.Key);
            await Store.WriteAsync(Manifest(id), document with { Enabled = enabled, ManagedLinks = managed }, NextJsonContext.Default.BridgeDocument, token);
        }
        document = document with { Enabled = enabled, ManagedLinks = managed };
        await Store.WriteAsync(Manifest(id), document, NextJsonContext.Default.BridgeDocument, token);
        if (ownershipConflicts.Count > 0)
            throw new HubException("Changed bridge items were preserved: " + string.Join(", ", ownershipConflicts) +
                ". Other owned links were revoked; resolve these conflicts before relying on the tightened policy.", "bridge_ownership_changed");
        if (!enabled) return document;
        foreach (var name in new[] { ".gitconfig", ".ssh" })
        {
            var target = Path.Combine(hostHome, name);
            var path = Path.Combine(paths.Home(id), name);
            if (!File.Exists(target) && !Directory.Exists(target)) continue;
            PathSafety.RejectExistingPathAncestors(target);
            if (managed.TryGetValue(name, out var existing))
            {
                if (File.Exists(path) || Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
                {
                    if (!IsManaged(path, name, existing)) throw new HubException("A managed bridge item changed; refusing to replace it.", "bridge_ownership_changed");
                    continue;
                }
            }
            if (File.Exists(path) || Directory.Exists(path) || new DirectoryInfo(path).LinkTarget is not null)
                throw new HubException("The profile already has an unmanaged " + name + "; preserved.", "bridge_conflict");
            // Persist intent before creating the item; a retry after interruption can finish safely.
            managed[name] = target;
            document = document with { ManagedLinks = managed };
            await Store.WriteAsync(Manifest(id), document, NextJsonContext.Default.BridgeDocument, token);
            if (name == ".gitconfig") await NextConfiguration.WriteTextAtomicAsync(paths.DataRoot, path, GitInclude(target), token);
            else await CreateJunctionAsync(path, target, token);
        }
        return document;
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static bool IsManaged(string path, string name, string target) => name == ".gitconfig"
        ? File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 && File.ReadAllText(path) == GitInclude(target)
        : new DirectoryInfo(path).LinkTarget is { } link && SamePath(link, target);
    private static void RemoveManaged(string path, string name, string target)
    {
        if (!File.Exists(path) && !Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null) return;
        if (!IsManaged(path, name, target)) throw new HubException("Bridge ownership changed; preserved item and host target.", "bridge_ownership_changed");
        if (name == ".gitconfig") File.Delete(path);
        else Directory.Delete(path, recursive: false); // Deletes only the junction entry, never its target.
    }
    private static async Task CreateJunctionAsync(string path, string target, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + Quote(path) + " -Target " + Quote(target) + " | Out-Null";
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new HubException("Bridge creation failed.", "bridge_create_failed");
        var stdout = process.StandardOutput.ReadToEndAsync(token); var stderr = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token); await stdout; await stderr;
        if (process.ExitCode != 0) throw new HubException("SSH junction creation failed; retry or disable bridging.", "bridge_create_failed");
    }
}
