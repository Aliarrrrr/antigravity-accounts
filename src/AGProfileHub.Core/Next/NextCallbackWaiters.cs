namespace AGProfileHub.Core;

internal interface ICallbackWaiterStore
{
    Task<CallbackQueue> ReadAsync(CancellationToken token);
    Task WriteAsync(CallbackQueue queue, CancellationToken token);
}

internal sealed class AtomicCallbackWaiterStore(HubPaths paths) : ICallbackWaiterStore
{
    private string Path => System.IO.Path.Combine(paths.DataRoot, "runtime", "login-waiters.json");
    public Task<CallbackQueue> ReadAsync(CancellationToken token) => new AtomicJsonStore(paths.DataRoot)
        .ReadAsync(Path, NextJsonContext.Default.CallbackQueue, () => new CallbackQueue(), token);
    public Task WriteAsync(CallbackQueue queue, CancellationToken token) => new AtomicJsonStore(paths.DataRoot)
        .WriteAsync(Path, queue, NextJsonContext.Default.CallbackQueue, token);
}
