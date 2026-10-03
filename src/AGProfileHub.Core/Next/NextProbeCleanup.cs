namespace AGProfileHub.Core;

internal static class NextProbeCleanup
{
    internal static void DeleteOwnedScratch(HubPaths parent, string scratch, bool processesStopped, bool protocolRestored, bool proofSaved)
    {
        if (!processesStopped || !protocolRestored || !proofSaved)
            throw new HubException("Probe recovery evidence must be retained.", "probe_cleanup_refused");
        var probes = Path.GetFullPath(Path.Combine(parent.DataRoot, "probes"));
        scratch = Path.GetFullPath(scratch);
        if (!NextHubService.EqualPath(Path.GetDirectoryName(scratch), probes) ||
            !Guid.TryParseExact(Path.GetFileName(scratch), "N", out _))
            throw new HubException("Only this operation's probe directory may be removed.", "probe_cleanup_refused");
        PathSafety.RejectExistingReparsePoints(parent.DataRoot, scratch);
        // Inspect each entry before descending; never traverse a junction during validation.
        var directories = new Stack<string>(); directories.Push(scratch);
        while (directories.TryPop(out var directory))
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                PathSafety.RejectExistingReparsePoints(parent.DataRoot, entry);
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        PathSafety.RejectExistingReparsePoints(parent.DataRoot, scratch);
        Directory.Delete(scratch, recursive: true);
    }
}
