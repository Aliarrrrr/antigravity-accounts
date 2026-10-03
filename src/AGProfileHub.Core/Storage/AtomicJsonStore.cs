using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AGProfileHub.Core;

internal sealed class AtomicJsonStore(string managedRoot)
{
    public async Task<T> ReadAsync<T>(string path, JsonTypeInfo<T> typeInfo, Func<T> defaultFactory, CancellationToken cancellationToken)
    {
        PathSafety.EnsureContained(managedRoot, path);
        PathSafety.RejectExistingReparsePoints(managedRoot, path);
        if (!File.Exists(path)) return defaultFactory();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaElement) ||
                schemaElement.ValueKind != JsonValueKind.Number || !schemaElement.TryGetInt32(out var schema))
                throw new ConfigurationException($"Configuration '{path}' has no valid schemaVersion.", "schema_missing");
            if (schema > SchemaVersions.Current) throw new ConfigurationException($"Configuration schema {schema} is newer than supported schema {SchemaVersions.Current}.", "future_schema");
            if (schema < 1) throw new ConfigurationException($"Configuration schema {schema} is invalid.", "schema_invalid");
            return document.Deserialize(typeInfo) ?? throw new ConfigurationException($"Configuration '{path}' is empty.", "configuration_empty");
        }
        catch (JsonException ex) { throw new ConfigurationException($"Configuration '{path}' contains invalid JSON and was not replaced.", "configuration_corrupt", ex); }
    }

    public async Task WriteAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)!;
        PathSafety.EnsureContained(managedRoot, path);
        PathSafety.RejectExistingReparsePoints(managedRoot, path);
        PathSafety.RejectExistingReparsePoints(managedRoot, path + ".bak");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backupTemporary = temporary + ".backup";
        Exception? failure = null;
        try
        {
            PathSafety.EnsureSafeDirectory(managedRoot, directory);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(stream, value, typeInfo, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            PathSafety.RejectExistingReparsePoints(managedRoot, path);
            PathSafety.RejectExistingReparsePoints(managedRoot, path + ".bak");
            if (File.Exists(path))
            {
                // Replace the backup directory entry instead of writing through an existing link.
                await RetryContentionAsync(() => File.Copy(path, backupTemporary, overwrite: false), cancellationToken).ConfigureAwait(false);
                await RetryContentionAsync(() => File.Move(backupTemporary, path + ".bak", overwrite: true), cancellationToken).ConfigureAwait(false);
            }
            await RetryContentionAsync(() => File.Move(temporary, path, overwrite: true), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var classified = new ConfigurationException("Configuration could not be saved.", "configuration_write_failed", ex);
            failure = classified;
            throw classified;
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            List<Exception>? cleanupFailures = null;
            foreach (var ownedTemporary in new[] { temporary, backupTemporary })
            {
                try
                {
                    PathSafety.RejectExistingReparsePoints(managedRoot, ownedTemporary);
                    if (File.Exists(ownedTemporary)) File.Delete(ownedTemporary);
                }
                catch (Exception ex) { (cleanupFailures ??= []).Add(ex); }
            }
            if (cleanupFailures is not null)
            {
                var cleanup = new AggregateException("Owned configuration temporary cleanup failed.", cleanupFailures);
                if (failure is not null) failure.Data["CleanupFailures"] = cleanup;
                else
                {
                    // Preserve known path contracts; standalone IO still obeys the save contract.
                    var first = cleanupFailures[0];
                    var classified = first is IOException or UnauthorizedAccessException
                        ? new ConfigurationException("Configuration temporary cleanup failed.", "configuration_write_failed", first)
                        : first;
                    classified.Data["CleanupFailures"] = cleanup;
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(classified).Throw();
                }
            }
        }
    }

    private static async Task RetryContentionAsync(Action commit, CancellationToken token)
    {
        // Windows readers frequently omit delete sharing. Retry only native contention codes.
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { commit(); return; }
            catch (Exception ex) when (OperatingSystem.IsWindows() &&
                ex is IOException or UnauthorizedAccessException &&
                ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021) or unchecked((int)0x80070005) && attempt < 9)
            { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }
}
