namespace AGProfileHub.Core;

internal sealed class FileLock : IAsyncDisposable
{
    private readonly FileStream _stream;
    private FileLock(FileStream stream) => _stream = stream;

    public static async Task<FileLock> AcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new ConfigurationException("Lock path has no parent.", "lock_path_invalid");
        PathSafety.RejectExistingPathAncestors(parent);
        Directory.CreateDirectory(parent);
        PathSafety.RejectExistingPathAncestors(parent);
        var deadline = DateTime.UtcNow + timeout;
        IOException? last = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                PathSafety.RejectExistingPathAncestors(fullPath);
                return new FileLock(new FileStream(fullPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous));
            }
            catch (IOException ex) { last = ex; await Task.Delay(50, cancellationToken).ConfigureAwait(false); }
        } while (DateTime.UtcNow < deadline);
        throw new ConfigurationException($"Timed out acquiring lock '{Path.GetFileName(path)}'.", "lock_timeout", last);
    }

    public async ValueTask DisposeAsync() => await _stream.DisposeAsync().ConfigureAwait(false);
}
