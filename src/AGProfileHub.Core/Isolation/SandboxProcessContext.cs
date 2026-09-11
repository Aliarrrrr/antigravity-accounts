using System.Runtime.InteropServices;
using System.Text;

namespace AGProfileHub.Core;

/// <summary>
/// Answers "which Sandboxie box is this process actually running inside?", using
/// only the module Sandboxie has already injected into the process.
/// </summary>
/// <remarks>
/// <para>
/// A command-line switch such as <c>--box</c> is a request, never evidence: any
/// caller can pass one. The only trustworthy source is the running box reported
/// by the injected <c>SbieDll.dll</c>, so the credential shim asks this type
/// before it touches the vendor credential target.
/// </para>
/// <para>
/// The entry point is resolved with <c>GetModuleHandle</c> plus
/// <c>GetProcAddress</c> rather than a search-path load, so the working
/// directory cannot substitute a different module.
/// </para>
/// </remarks>
public static class SandboxProcessContext
{
    /// <summary>
    /// Returns the name of the box this process runs in, or <c>null</c> when the
    /// process is not sandboxed (or Sandboxie cannot be queried).
    /// </summary>
    public static string? CurrentBox()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var module = GetModuleHandle("SbieDll.dll");
        if (module == nint.Zero) return null;
        var entry = GetProcAddress(module, "SbieApi_QueryProcess");
        if (entry == nint.Zero) return null;
        var query = Marshal.GetDelegateForFunctionPointer<QueryProcess>(entry);
        var box = new StringBuilder(256);
        var image = new StringBuilder(256);
        var sid = new StringBuilder(256);
        return query((nint)Environment.ProcessId, box, image, sid, out _) == 0 ? box.ToString() : null;
    }

    /// <summary>
    /// Fails closed unless this process runs inside a Sandboxie box and, when
    /// <paramref name="expectedBox"/> is supplied, inside that exact box.
    /// </summary>
    /// <param name="expectedBox">
    /// The box the caller believes it was launched in. An unrelated box is not
    /// good enough: it could be a box the user deliberately keeps unisolated, and
    /// an entry claimed there would not protect the intended profile.
    /// </param>
    public static void RequireBox(string? expectedBox = null)
    {
        var actual = CurrentBox();
        if (string.IsNullOrWhiteSpace(actual) ||
            expectedBox is not null && !string.Equals(actual, expectedBox, StringComparison.OrdinalIgnoreCase))
            throw new HubException(
                "credential-shim must run inside the verified Sandboxie box. No host credentials were changed.",
                "sandbox_context_required");
    }

    // Upstream declaration: Sandboxie/core/dll/sbieapi.h, SbieApi_QueryProcess.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int QueryProcess(nint processId, StringBuilder boxName, StringBuilder imageName,
        StringBuilder sid, out uint sessionId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "GetModuleHandleW")]
    private static extern nint GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint GetProcAddress(nint module, string name);
}
