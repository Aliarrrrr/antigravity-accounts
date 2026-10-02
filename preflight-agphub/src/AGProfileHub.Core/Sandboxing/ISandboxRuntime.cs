namespace AGProfileHub.Core;

public sealed record SandboxRuntimeSnapshot(
    int ProcessId,
    string Name,
    string? ExecutablePath);

/// <summary>
/// Abstraction over an external isolation runtime (Sandboxie Start.exe / SbieIni.exe).
/// Product code never treats the launcher exit code as proof the vendor app is running.
/// </summary>
public interface ISandboxRuntime
{
    bool IsAvailable { get; }
    string RuntimeDescription { get; }
    Task<bool> EnsureBoxAsync(string boxId, IReadOnlyList<string> openFilePaths, CancellationToken cancellationToken = default);
    Task<int> RunInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task StartInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SandboxRuntimeSnapshot>> ListProcessesAsync(string boxId, CancellationToken cancellationToken = default);
    Task TerminateBoxAsync(string boxId, CancellationToken cancellationToken = default);
}

/// <summary>Null sandbox: profiles marked Sandboxie cannot launch without a real runtime.</summary>
public sealed class UnsupportedSandboxRuntime : ISandboxRuntime
{
    public bool IsAvailable => false;
    public string RuntimeDescription => "No isolation runtime is configured.";
    public Task<bool> EnsureBoxAsync(string boxId, IReadOnlyList<string> openFilePaths, CancellationToken cancellationToken = default) =>
        throw new UnsupportedCapabilityException("This profile requires Sandboxie, which is not available. Install Sandboxie-Plus or use the Direct backend.");
    public Task<int> RunInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new UnsupportedCapabilityException("This profile requires Sandboxie, which is not available.");
    public Task StartInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken = default) =>
        throw new UnsupportedCapabilityException("This profile requires Sandboxie, which is not available.");
    public Task<IReadOnlyList<SandboxRuntimeSnapshot>> ListProcessesAsync(string boxId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SandboxRuntimeSnapshot>>([]);
    public Task TerminateBoxAsync(string boxId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
