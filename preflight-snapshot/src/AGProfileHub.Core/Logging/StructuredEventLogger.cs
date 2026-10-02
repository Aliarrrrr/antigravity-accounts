using System.Text.Json;

namespace AGProfileHub.Core;

public sealed record HubLogEvent(DateTimeOffset Timestamp, string Operation, Guid? ProfileId, string? AntigravityVersion,
    string? SanitizedCommandTemplate, int? ProcessId, string Result, string? ErrorCode);

public interface IHubLogger
{
    Task WriteAsync(HubLogEvent entry, CancellationToken cancellationToken = default);
}

public sealed class StructuredEventLogger(HubPaths paths) : IHubLogger
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task WriteAsync(HubLogEvent entry, CancellationToken cancellationToken = default)
    {
        paths.EnsureBaseLayout();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var held = await FileLock.AcquireAsync(paths.LogsLock, TimeSpan.FromSeconds(5), cancellationToken);
            var path = Path.Combine(paths.LogsRoot, $"hub-{entry.Timestamp:yyyy-MM}.jsonl");
            PathSafety.RejectExistingReparsePoints(paths.DataRoot, path);
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}

public sealed class NullHubLogger : IHubLogger
{
    public Task WriteAsync(HubLogEvent entry, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
