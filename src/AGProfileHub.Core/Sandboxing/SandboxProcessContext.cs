using System.Runtime.InteropServices;
using System.Text;

namespace AGProfileHub.Core;

/// <summary>Uses only the already injected Sandboxie module, never loads a DLL by search path.</summary>
public static class SandboxProcessContext
{
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

    public static void RequireBox(string? expectedBox = null)
    {
        var actual = CurrentBox();
        if (string.IsNullOrWhiteSpace(actual) ||
            expectedBox is not null && !string.Equals(actual, expectedBox, StringComparison.OrdinalIgnoreCase))
            throw new HubException("credential-shim must run inside the verified Sandboxie box. No host credentials were changed.", "sandbox_context_required");
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
