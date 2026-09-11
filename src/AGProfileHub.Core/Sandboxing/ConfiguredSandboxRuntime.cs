namespace AGProfileHub.Core;

/// <summary>Loads the optional backend configuration only when it is used.</summary>
internal sealed class ConfiguredSandboxRuntime(ConfigRepository repository) : ISandboxRuntime
{
    // Availability is checked with current settings by each async operation. Avoid
    // blocking the WPF synchronization context on configuration I/O here.
    public bool IsAvailable => OperatingSystem.IsWindows();
    public string RuntimeDescription => "Sandboxie is optional. Configure its installation directory or use the Direct backend.";

    private async Task<WindowsSandboxieRuntime> ResolveAsync(CancellationToken cancellationToken)
    {
        var settings = await repository.ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var runtime = new WindowsSandboxieRuntime(settings.SandboxieHome);
        if (!runtime.IsAvailable) throw new UnsupportedCapabilityException(runtime.RuntimeDescription);
        return runtime;
    }

    public async Task<bool> EnsureBoxAsync(string boxId, IReadOnlyList<string> openFilePaths, CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(cancellationToken).ConfigureAwait(false)).EnsureBoxAsync(boxId, openFilePaths, cancellationToken).ConfigureAwait(false);

    public async Task<int> RunInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(cancellationToken).ConfigureAwait(false)).RunInBoxAsync(boxId, executablePath, arguments, environment, timeout, cancellationToken).ConfigureAwait(false);

    public async Task StartInBoxAsync(string boxId, string executablePath, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(cancellationToken).ConfigureAwait(false)).StartInBoxAsync(boxId, executablePath, arguments, environment, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<SandboxRuntimeSnapshot>> ListProcessesAsync(string boxId, CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(cancellationToken).ConfigureAwait(false)).ListProcessesAsync(boxId, cancellationToken).ConfigureAwait(false);

    public async Task TerminateBoxAsync(string boxId, CancellationToken cancellationToken = default) =>
        await (await ResolveAsync(cancellationToken).ConfigureAwait(false)).TerminateBoxAsync(boxId, cancellationToken).ConfigureAwait(false);
}
