namespace AGProfileHub.Core;

/// <summary>
/// Creates the isolation backend a Profile asked for.
/// </summary>
/// <remarks>
/// Both backends are described by the same interface and the same scope naming,
/// so the launcher and its state file do not need to know which one is in use.
/// </remarks>
public static class IsolationBackends
{
    /// <summary>Resolves a backend or explains why it cannot be used.</summary>
    public static IIsolationBackend Create(HubPaths paths, IsolationBackendKind kind) => kind switch
    {
        IsolationBackendKind.Sandboxie => SandboxieBackend.TryDiscover()
            ?? throw new HubException("Sandboxie Plus was not found. Isolation-scoped launch requires it.", "sandbox_not_found"),
        IsolationBackendKind.ProcessLocal => new ProcessLocalBackend(paths),
        _ => throw new ConfigurationException("Unknown isolation backend.", "invalid_isolation_backend")
    };

    /// <summary>
    /// Same as <see cref="Create"/> but reports an unavailable backend as
    /// <c>null</c>, for status paths that must describe the situation instead of
    /// failing. An unknown backend is still a configuration error.
    /// </summary>
    public static IIsolationBackend? TryCreate(HubPaths paths, IsolationBackendKind kind) => kind switch
    {
        IsolationBackendKind.Sandboxie => SandboxieBackend.TryDiscover(),
        IsolationBackendKind.ProcessLocal => new ProcessLocalBackend(paths),
        _ => throw new ConfigurationException("Unknown isolation backend.", "invalid_isolation_backend")
    };

    /// <summary>The scope name a Profile uses under the given backend.</summary>
    public static string ScopeNameFor(Guid profileId) => ProcessLocalBackend.ScopeNameFor(profileId);
}
