namespace AGProfileHub.Core;

/// <summary>
/// SHA-256 pinning for the configured Antigravity executable.
/// </summary>
/// <remarks>
/// The Hub launches and deep-link-relaunches one vendor binary, so that binary
/// is pinned the same way the language server is pinned by
/// <see cref="AgentRuntimeProbe"/>: the digest is recorded when the executable
/// is configured and verified before every use. A silently swapped binary is
/// refused instead of launched. Pinning begins at the moment of configuration;
/// it cannot retroactively judge what the user chose to configure.
/// </remarks>
public static class ExecutableAttestation
{
    /// <summary>Computes the lowercase hex SHA-256 digest of a file.</summary>
    /// <remarks>
    /// The file is held open with <c>FileShare.Read</c> while it is hashed, so
    /// the digest and the verification cannot race a concurrent replacement.
    /// </remarks>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Fails closed unless the executable still matches the pinned digest.
    /// A missing pin verifies trivially: executables configured before pinning
    /// existed keep working, and doctor points at reconfiguring.
    /// </summary>
    public static async Task VerifyAsync(string path, string? pinnedSha256, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pinnedSha256)) return;
        var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, pinnedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new HubException(
                "The Antigravity executable no longer matches the digest recorded when it was configured, " +
                "so the Hub refuses to launch or relaunch it. Re-run configure-executable if the vendor updated it.",
                "executable_attestation_changed");
    }

    /// <summary>
    /// Fails closed when the executable was never pinned. Discovery and doctor
    /// may still inspect an unpinned install; launch and deep-link handoff may not.
    /// </summary>
    public static void RequirePinned(string? pinnedSha256)
    {
        if (!string.IsNullOrWhiteSpace(pinnedSha256)) return;
        throw new HubException(
            "The Antigravity executable is not hash-pinned, so a swapped binary cannot be refused. " +
            "Run 'configure-executable <path>' once, then launch again.",
            "executable_not_pinned");
    }
}
