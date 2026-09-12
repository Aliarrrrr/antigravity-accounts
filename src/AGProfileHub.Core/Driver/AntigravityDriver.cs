using System.Diagnostics;

namespace AGProfileHub.Core;

public sealed record AntigravityInstallation(string ExecutablePath, string Version, bool IsKnownCompatible, string CompatibilityDetail);

public interface IAntigravityDriver
{
    Task<AntigravityInstallation> DiscoverAsync(CancellationToken cancellationToken = default);
    LaunchPlan BuildLaunchPlan(AntigravityInstallation installation, Guid profileId,
        IsolationBackendKind backend = IsolationBackendKind.Sandboxie);
}

public sealed class AntigravityDriver(HubPaths paths, ConfigRepository repository) : IAntigravityDriver
{
    public async Task<AntigravityInstallation> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var settings = await repository.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.AntigravityExecutablePath)) candidates.Add(settings.AntigravityExecutablePath);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Programs", "antigravity", "Antigravity.exe"));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles)) candidates.Add(Path.Combine(programFiles, "Antigravity", "Antigravity.exe"));

        string? executable = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) { executable = full; break; }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                if (string.Equals(candidate, settings.AntigravityExecutablePath, StringComparison.OrdinalIgnoreCase))
                    throw new ConfigurationException("Configured Antigravity executable path is invalid.", "executable_path_invalid", ex);
            }
        }
        if (executable is null)
            throw new HubException("Antigravity Desktop was not found. Configure an explicit executable path.", "executable_not_found");
        executable = PathSafety.CanonicalizeExistingFile(executable);
        var info = FileVersionInfo.GetVersionInfo(executable);
        var version = info.ProductVersion ?? info.FileVersion ?? "unknown";
        var normalized = NormalizeVersion(version);
        var compatible = normalized == "2.12.2";
        return new AntigravityInstallation(executable, normalized, compatible,
            compatible ? "Desktop 2.12.2 matches the locally observed experimental launch strategy." :
            $"Version {normalized} has not been probed. Refusing to guess the isolation contract.");
    }

    /// <param name="backend">
    /// The Profile's isolation backend. Only the process-local backend needs
    /// anything extra here: the sandbox backend already owns a private
    /// credential scope, so its launch plan must stay exactly as it was.
    /// </param>
    public LaunchPlan BuildLaunchPlan(AntigravityInstallation installation, Guid profileId,
        IsolationBackendKind backend = IsolationBackendKind.Sandboxie)
    {
        if (!installation.IsKnownCompatible) throw new HubException(installation.CompatibilityDetail, "version_unverified");
        paths.EnsureProfileLayout(profileId);
        var home = paths.Home(profileId);
        var root = Path.GetPathRoot(home) ?? string.Empty;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = home,
            ["HOME"] = home,
            ["HOMEDRIVE"] = root.TrimEnd(Path.DirectorySeparatorChar),
            ["HOMEPATH"] = home.Length >= 3 && home[1] == ':' ? home[2..] : home,
            ["APPDATA"] = paths.Roaming(profileId),
            ["LOCALAPPDATA"] = paths.Local(profileId),
            ["TEMP"] = paths.Temp(profileId),
            ["TMP"] = paths.Temp(profileId)
        };
        if (backend == IsolationBackendKind.ProcessLocal)
        {
            // The language server picks its credential store by probing for a
            // container, SSH or WSL session. Reporting one keeps the instance on
            // the token file inside this Profile's own home instead of the
            // Windows user's single shared credential entry. The value is only a
            // label: the detector looks at presence, not content.
            environment["WSL_DISTRO_NAME"] = "AGPH-" + profileId.ToString("N")[..8];
        }
        else if (backend != IsolationBackendKind.Sandboxie)
        {
            throw new ConfigurationException("Unknown isolation backend.", "invalid_isolation_backend");
        }

        return new LaunchPlan(installation.ExecutablePath, installation.Version,
            [$"--user-data-dir={paths.UserData(profileId)}"], environment,
            "Antigravity.exe --user-data-dir=<managed-profile-user-data>");
    }

    private static string NormalizeVersion(string version)
    {
        var numeric = new string(version.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray()).Trim('.');
        return Version.TryParse(numeric, out var parsed) ? $"{parsed.Major}.{parsed.Minor}.{Math.Max(parsed.Build, 0)}" : version;
    }
}
