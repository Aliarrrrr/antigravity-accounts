using System.Diagnostics;
using System.Security.Cryptography;

namespace AGProfileHub.Core;

public static class NextCompatibility
{
    public static IEnumerable<string> DiscoveryCandidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) { yield return configured; yield break; }
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "Antigravity.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Antigravity", "Antigravity.exe");
    }
    public static async Task<InstallationFingerprint> FingerprintAsync(string executable, CancellationToken token = default)
    {
        executable = PathSafety.CanonicalizeExistingFile(executable);
        var directory = Path.GetDirectoryName(executable)!;
        var files = new List<string> { executable, Path.Combine(directory, "resources", "app.asar"),
            Path.Combine(directory, "resources", "bin", "language_server.exe") };
        files.AddRange(Directory.EnumerateFiles(directory, "*.dll"));
        files.AddRange(Directory.EnumerateFiles(Path.Combine(directory, "resources", "bin"), "*.exe"));
        var unpacked = Path.Combine(directory, "resources", "app.asar.unpacked");
        if (Directory.Exists(unpacked)) files.AddRange(Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories));
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            PathSafety.CanonicalizeExistingFile(file);
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            hashes[Path.GetRelativePath(directory, file)] = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        }
        return new InstallationFingerprint(executable, FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "unknown", hashes);
    }

    public static bool Same(InstallationFingerprint? left, InstallationFingerprint? right) => left is not null && right is not null &&
        string.Equals(left.Executable, right.Executable, StringComparison.OrdinalIgnoreCase) && left.Version == right.Version &&
        left.Components.Count == right.Components.Count && left.Components.All(pair =>
            right.Components.TryGetValue(pair.Key, out var value) && string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase));

    public static bool PermitsLaunch(CompatibilityProof? proof, InstallationFingerprint current) =>
        proof is { StrategyRevision: NextHubService.StrategyRevision, FileTokenStore: true, IndependentHome: true,
            IndependentUserData: true, ProcessesStopped: true, ProtocolRestored: true } &&
        proof.ProbedUtc != default && Same(proof.Installation, current);

    internal static async Task<IDisposable> HoldAsync(InstallationFingerprint expected, CancellationToken token)
    {
        var streams = new List<FileStream>();
        try
        {
            foreach (var component in expected.Components)
            {
                var path = Path.Combine(Path.GetDirectoryName(expected.Executable)!, component.Key);
                PathSafety.CanonicalizeExistingFile(path);
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); streams.Add(stream);
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), component.Value, StringComparison.OrdinalIgnoreCase))
                    throw new HubException("Runtime component changed; probe again.", "installation_changed");
            }
            return new Lease(streams);
        }
        catch { foreach (var stream in streams) stream.Dispose(); throw; }
    }
    private sealed class Lease(List<FileStream> streams) : IDisposable
    { public void Dispose() { foreach (var stream in streams) stream.Dispose(); } }
}
