using System.Runtime.InteropServices;

namespace AGProfileHub.Core;

/// <summary>
/// Metadata about a Windows Credential Manager entry. The credential blob is
/// deliberately never surfaced: callers reason about existence and size only.
/// </summary>
public sealed record CredentialEntryInfo(string Target, bool Exists, int BlobSize, string? UserName)
{
    public static CredentialEntryInfo Missing(string target) => new(target, false, 0, null);
}

/// <summary>
/// Minimal read-mostly wrapper over the Win32 credential APIs.
/// </summary>
/// <remarks>
/// The credential blob is never copied out of native memory. This type exists so
/// the Hub can answer "does this account-scoped entry exist here, and how big is
/// it" without ever handling secret material.
/// </remarks>
public static class WindowsCredentialStore
{
    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    public static CredentialEntryInfo GetInfo(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ConfigurationException("Credential target is required.", "credential_target_missing");

        if (!CredReadW(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return CredentialEntryInfo.Missing(target);
            throw new HubException($"Credential lookup failed with Win32 error {error}.", "credential_read_failed");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return new CredentialEntryInfo(target, true, credential.CredentialBlobSize, credential.UserName);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <summary>
    /// Creates the entry only when it is absent. Returns true when it was created.
    /// </summary>
    public static bool EnsurePlaceholder(string target, string userName, byte[] payload)
    {
        if (GetInfo(target).Exists) return false;
        WritePlaceholder(target, userName, payload);
        return true;
    }

    /// <summary>
    /// Writes unconditionally, overwriting any existing entry.
    /// </summary>
    /// <remarks>
    /// Required inside isolation scopes that fall through to the host store on
    /// reads: there, a delete only removes the scope-local copy and the next
    /// read still sees the host entry, so an existence check never reports
    /// "missing". Only callers that know the scope is brand new may use this.
    /// </remarks>
    public static void WritePlaceholder(string target, string userName, byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ConfigurationException("Credential target is required.", "credential_target_missing");
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0) throw new ConfigurationException("Placeholder payload cannot be empty.", "credential_payload_empty");

        var buffer = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, buffer, payload.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = userName,
                CredentialBlobSize = payload.Length,
                CredentialBlob = buffer,
                Persist = CredPersistLocalMachine
            };
            if (!CredWriteW(ref credential, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new HubException($"Credential write failed with Win32 error {error}.", "credential_write_failed");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool Delete(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) throw new ConfigurationException("Credential target is required.", "credential_target_missing");
        if (!CredDeleteW(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return false;
            throw new HubException($"Credential delete failed with Win32 error {error}.", "credential_delete_failed");
        }
        return true;
    }
}
