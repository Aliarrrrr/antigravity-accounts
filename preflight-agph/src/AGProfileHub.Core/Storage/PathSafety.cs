namespace AGProfileHub.Core;

public static class PathSafety
{
    public static void RejectExistingPathAncestors(string path)
    {
        var full = FullPath(path, "path_invalid");
        var cursor = Path.GetPathRoot(full)!;
        CheckExistingComponent(cursor);
        foreach (var component in full[cursor.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, component);
            CheckExistingComponent(cursor);
        }
    }

    private static void CheckExistingComponent(string path)
    {
        try
        {
            // File.GetAttributes also identifies file links and dangling links; Exists may hide them.
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ConfigurationException($"Managed path contains a reparse point: {path}", "reparse_point_rejected");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    public static string EnsureContained(string root, string candidate)
    {
        var canonicalRoot = WithSeparator(FullPath(root, "managed_root_invalid"));
        var canonicalCandidate = FullPath(candidate, "path_invalid");
        if (!WithSeparator(canonicalCandidate).StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException($"Path is outside the managed data root: {canonicalCandidate}", "path_outside_root");
        return canonicalCandidate;
    }

    public static void EnsureSafeDirectory(string root, string path)
    {
        var safe = EnsureContained(root, path);
        RejectExistingReparsePoints(root, safe);
        Directory.CreateDirectory(safe);
        RejectExistingReparsePoints(root, safe);
    }

    public static void RejectExistingReparsePoints(string root, string path)
    {
        var canonicalPath = EnsureContained(root, path);
        RejectExistingPathAncestors(canonicalPath);
    }

    public static string? NormalizeWorkspace(string? workspace) => string.IsNullOrWhiteSpace(workspace)
        ? null : FullPath(workspace.Trim(), "invalid_workspace_path");

    public static string CanonicalizeExistingFile(string path)
    {
        var full = FullPath(path, "executable_path_invalid");
        RejectExistingPathAncestors(full);
        if (!File.Exists(full)) throw new ConfigurationException($"Executable does not exist: {full}", "executable_not_found");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new ConfigurationException("Executable path cannot be a reparse point.", "reparse_point_rejected");
        return full;
    }

    private static string WithSeparator(string path) => path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static string FullPath(string path, string code)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ConfigurationException("Path is invalid.", code, ex);
        }
    }
}
