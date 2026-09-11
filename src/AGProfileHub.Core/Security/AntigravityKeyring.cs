using System.Text;

namespace AGProfileHub.Core;

/// <summary>
/// The vendor language server stores its Google OAuth token in a single Windows
/// Credential Manager entry whose target is a fixed literal
/// (<c>gemini:antigravity</c>). Because the key is not derived from the profile
/// directory or any CLI switch, every instance launched by the same Windows user
/// shares one token: the most recent sign-in wins.
/// </summary>
/// <remarks>
/// The Hub never reads, copies or injects that token. Isolation is achieved by
/// making each isolation scope (for example a sandbox) own an entry of the same
/// name, so the scope stops falling through to the host copy.
/// </remarks>
public static class AntigravityKeyring
{
    public const string Target = "gemini:antigravity";
    public const string UserName = "antigravity";

    /// <summary>Prefix reserved for synthetic self-test entries.</summary>
    public const string ProbePrefix = "AGProfileHub.Probe.";

    /// <summary>
    /// A payload that is deliberately not a valid token. When the vendor code
    /// reads it, parsing fails and it falls back to its own file-based token
    /// store, which lives under the instance's isolated data directory.
    /// </summary>
    public static byte[] PlaceholderPayload() =>
        Encoding.UTF8.GetBytes("AGProfileHub:keyring-isolation-placeholder");

    public static CredentialEntryInfo DescribeLocalEntry() => WindowsCredentialStore.GetInfo(Target);

    public static bool IsProbeTarget(string target) =>
        !string.IsNullOrEmpty(target) && target.StartsWith(ProbePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Ensures this isolation scope owns an entry with the vendor target name,
    /// so credential lookups stop falling through to the host copy.
    /// </summary>
    /// <param name="forceFresh">
    /// Overwrite even when an entry is visible. Only safe for a brand-new
    /// isolation scope that has never signed in, because a visible entry there
    /// can only be the host copy seen through a fall-through read.
    /// </param>
    public static KeyringShimResult EnsureIsolation(bool forceFresh = false, string? expectedBox = null)
    {
        // This check must precede even the metadata query: a CLI argument alone
        // is never evidence of OS-level isolation.
        SandboxProcessContext.RequireBox(expectedBox);
        var before = WindowsCredentialStore.GetInfo(Target);
        var created = false;
        if (forceFresh)
        {
            WindowsCredentialStore.WritePlaceholder(Target, UserName, PlaceholderPayload());
            created = true;
        }
        else if (!before.Exists)
        {
            WindowsCredentialStore.EnsurePlaceholder(Target, UserName, PlaceholderPayload());
            created = true;
        }
        var after = WindowsCredentialStore.GetInfo(Target);
        return new KeyringShimResult(Target, before.Exists, created, after.BlobSize);
    }

    /// <summary>Reports host-scope state without ever reading the token.</summary>
    public static KeyringStatusResult BuildStatus()
    {
        var info = WindowsCredentialStore.GetInfo(Target);
        var belongsToVendor = info.Exists && string.Equals(info.UserName, UserName, StringComparison.Ordinal);
        var guidance = info.Exists
            ? "The Windows user owns one shared token entry. Instances launched outside an isolation scope will all read it, so the latest sign-in wins. Run each instance in a scope that owns its own entry of the same name."
            : "No shared host entry exists. Each instance will fall back to the vendor file-based token store inside its own data directory.";
        return new KeyringStatusResult(
            Target,
            info.Exists,
            info.BlobSize,
            belongsToVendor,
            guidance,
            "Only existence and size are reported. The token value is never read, copied or logged.");
    }
}

public sealed record KeyringShimResult(string Target, bool ExistedBefore, bool Created, int BlobSize);

public sealed record KeyringStatusResult(
    string Target,
    bool HostEntryExists,
    int HostEntryBlobSize,
    bool HostEntryBelongsToVendor,
    string IsolationGuidance,
    string Note);
