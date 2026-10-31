using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Antigravity.Accounts;

internal sealed class EncryptedAccountStore : IAccountStore
{
    private readonly string _root;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string StatePath => Path.Combine(_root, "accounts.v1.dpapi");

    public EncryptedAccountStore(string root, Func<byte[], byte[]>? protect = null,
        Func<byte[], byte[]>? unprotect = null)
    {
        if (!Path.IsPathFullyQualified(root)) throw new AccountOperationException("invalid_data_root", "Account data root must be an absolute path.");
        _root = Path.GetFullPath(root);
        _protect = protect ?? WindowsDataProtection.Protect;
        _unprotect = unprotect ?? WindowsDataProtection.Unprotect;
    }

    public async Task<SavedState> ReadAsync(CancellationToken token = default)
    {
        CheckAncestors(_root);
        if (!Directory.Exists(_root)) return new();
        await using var held = await AcquireAsync("store.lock", token);
        return ReadUnlocked();
    }

    public async Task UpdateAsync(Action<SavedState> update, CancellationToken token = default)
    {
        await using var held = await AcquireAsync("store.lock", token);
        var state = ReadUnlocked(out var migrated);
        update(state);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        byte[] encrypted;
        try { encrypted = _protect(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        var temporary = Path.Combine(_root, ".accounts." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await output.WriteAsync(encrypted, token);
                output.Flush(true);
            }
            CheckAncestors(StatePath);
            if (migrated) PreserveLegacyState();
            File.Move(temporary, StatePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<IAsyncDisposable> LockRefreshAsync(Guid id, CancellationToken token = default)
    {
        if (id == Guid.Empty) throw new AccountOperationException("account_not_found", "Account was not found.");
        return await AcquireAsync("refresh-" + id.ToString("N") + ".lock", token);
    }

    private SavedState ReadUnlocked() => ReadUnlocked(out _);

    private SavedState ReadUnlocked(out bool migrated)
    {
        migrated = false;
        CheckAncestors(StatePath);
        if (!File.Exists(StatePath)) return new();
        if (new FileInfo(StatePath).Length > 4 * 1024 * 1024)
            throw new AccountOperationException("store_invalid", "Account storage is invalid; it has not been replaced.");
        var encrypted = File.ReadAllBytes(StatePath);
        byte[] clear;
        try { clear = _unprotect(encrypted); }
        catch { throw new AccountOperationException("store_locked", "Account storage cannot be decrypted for this Windows user."); }
        try
        {
            var state = JsonSerializer.Deserialize<SavedState>(clear, Json);
            if (state is null || state.SchemaVersion is not (1 or 2) || state.Accounts is null || state.Accounts.Count > 100 ||
                state.Accounts.Any(a => a is null || a.Id == Guid.Empty || string.IsNullOrWhiteSpace(a.Subject) || a.Client is null || a.Token is null) ||
                state.Accounts.Select(a => a.Id).Distinct().Count() != state.Accounts.Count ||
                state.SelectedAccountId is { } selected && state.Accounts.All(a => a.Id != selected) ||
                state.PrimaryAccountId is { } primary && state.Accounts.All(a => a.Id != primary) || state.Generation < 0)
                throw new JsonException();
            if (state.SchemaVersion == 1)
            {
                // A legacy selection does not establish a primary identity. The user chooses it explicitly.
                state.PrimaryAccountId = null;
                state.Generation = 0;
                state.SchemaVersion = 2;
                migrated = true;
            }
            return state;
        }
        catch (JsonException) { throw new AccountOperationException("store_invalid", "Account storage is invalid; it has not been replaced."); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private void PreserveLegacyState()
    {
        var backup = Path.Combine(_root, "accounts.v1.before-v2.dpapi");
        CheckAncestors(backup);
        var original = File.ReadAllBytes(StatePath);
        if (File.Exists(backup))
        {
            if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(original))
                throw new AccountOperationException("migration_backup_conflict", "A different legacy account backup already exists; the current account file has not been replaced.");
            return;
        }
        using var output = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        output.Write(original);
        output.Flush(true);
    }

    private async Task<FileStream> AcquireAsync(string name, CancellationToken token)
    {
        CheckAncestors(_root);
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        CheckAncestors(path);
        var timeout = DateTime.UtcNow.AddSeconds(45);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < timeout) { await Task.Delay(40, token); }
            catch (IOException) { throw new AccountOperationException("store_busy", "Account storage is busy in another process."); }
        }
    }

    private static void CheckAncestors(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AccountOperationException("unsafe_data_root", "Account storage cannot use links or reparse points.");
    }
}

internal static class WindowsDataProtection
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Length; public nint Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy,
        nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy,
        nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);

    public static byte[] Protect(byte[] clear) => Transform(clear, true);
    public static byte[] Unprotect(byte[] encrypted) => Transform(encrypted, false);
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new AccountOperationException("windows_required", "Encrypted account storage requires Windows.");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var ok = protect
                ? CryptProtectData(ref input, "Antigravity independent accounts", 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!ok) throw new CryptographicException("Windows data protection failed.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (input.Data != 0) { Marshal.Copy(new byte[input.Length], 0, input.Data, input.Length); Marshal.FreeHGlobal(input.Data); }
            if (output.Data != 0) { Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length); LocalFree(output.Data); }
        }
    }
}
