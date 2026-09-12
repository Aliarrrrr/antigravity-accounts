namespace AGProfileHub.Core;

/// <summary>
/// Creates the isolation backend a Profile asked for.
/// </summary>
/// <remarks>
/// Sandboxie support was removed: the product no longer needs a sandbox
/// product, and Profiles recorded as <see cref="IsolationBackendKind.Sandboxie"/>
/// (created by older builds) run under the same process-local scope, whose
/// launch is verified to keep the instance off the shared Windows credential
/// entry before it is trusted.
/// </remarks>
public static class IsolationBackends
{
    /// <summary>Resolves a backend or explains why it cannot be used.</summary>
    public static IIsolationBackend Create(HubPaths paths, IsolationBackendKind kind) => new ProcessLocalBackend(paths);

    /// <summary>Resolves a backend, or <c>null</c> when the kind is unknown.</summary>
    public static IIsolationBackend? TryCreate(HubPaths paths, IsolationBackendKind kind) =>
        kind is IsolationBackendKind.Sandboxie or IsolationBackendKind.ProcessLocal ? new ProcessLocalBackend(paths) : null;

    /// <summary>The scope name a Profile uses under the backend.</summary>
    public static string ScopeNameFor(Guid profileId) => ProcessLocalBackend.ScopeNameFor(profileId);
}
