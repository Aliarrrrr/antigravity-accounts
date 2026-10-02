namespace AGProfileHub.Core;

/// <summary>
/// The mechanism a Profile's scope runs under. Every implementation names its
/// scope the same way and keeps its process list answerable, so the launcher
/// and its state file do not need to know which one is in use.
/// </summary>
public interface IIsolationBackend
{
    /// <summary>Whether the named scope exists and can be acted on.</summary>
    bool BoxExists(string scope);

    /// <summary>Creates the named scope. Throws when the scope could not be made.</summary>
    void CreateBox(string scope);

    /// <summary>Process IDs that belong to the named scope, root first.</summary>
    IReadOnlyList<int> ListPids(string scope);

    /// <summary>Stops every process in the named scope.</summary>
    void Terminate(string scope);

    /// <summary>Starts the executable inside the scope and returns the launcher's pid.</summary>
    int LaunchDetached(string scope, string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment);
}
