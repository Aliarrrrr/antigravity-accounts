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
        PathSafety.EnsureSafeDirectory(managedRoot, directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backupTemporary = temporary + ".backup";
        try
        {
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
                File.Copy(path, backupTemporary, overwrite: false);
                File.Move(backupTemporary, path + ".bak", overwrite: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            foreach (var ownedTemporary in new[] { temporary, backupTemporary })
            {
                PathSafety.RejectExistingReparsePoints(managedRoot, ownedTemporary);
                if (File.Exists(ownedTemporary)) File.Delete(ownedTemporary);
            }
        }
    }
}
