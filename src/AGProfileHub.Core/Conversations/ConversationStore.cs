using System.Text;

namespace AGProfileHub.Core;

public sealed record ConversationListItem(string Id, long SizeBytes, DateTimeOffset LastWriteUtc, string? Title);

public sealed record ConversationImportPlan(int FileCount, long TotalBytes, IReadOnlyList<string> Files, bool DestinationNotEmpty);

public sealed record ConversationImportResult(int ImportedCount, long TotalBytes, string? BackupDirectory, IReadOnlyList<string> Files);

/// <summary>
/// Read-only listing and copy-only import of vendor conversation databases.
/// Never parses conversation content into Hub logs and never touches tokens.
/// </summary>
public static class ConversationStore
{
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();

    public static string ConversationsDir(HubPaths paths, Guid profileId) =>
        Path.Combine(paths.Home(profileId), ".gemini", "antigravity", "conversations");

    public static string BrainDir(HubPaths paths, Guid profileId) =>
        Path.Combine(paths.Home(profileId), ".gemini", "antigravity", "brain");

    public static string DefaultSourceDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity");

    public static IReadOnlyList<ConversationListItem> List(HubPaths paths, Guid profileId)
    {
        var dir = ConversationsDir(paths, profileId);
        if (!Directory.Exists(dir)) return [];
        var items = new List<ConversationListItem>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.db").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            var id = Path.GetFileNameWithoutExtension(info.Name);
            string? title = null;
            try { title = TryReadTitle(info.FullName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { title = null; }
            items.Add(new ConversationListItem(id, info.Length, info.LastWriteTimeUtc, title));
        }
        return items;
    }

    public static ConversationImportPlan PlanImport(HubPaths paths, Guid profileId, string sourceDir)
    {
        var source = ResolveSource(sourceDir);
        var files = CollectImportFiles(source);
        var dest = ConversationsDir(paths, profileId);
        var destNotEmpty = Directory.Exists(dest) && Directory.EnumerateFiles(dest, "*.db").Any();
        return new ConversationImportPlan(files.Count, files.Sum(f => new FileInfo(f).Length),
            files.Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList(), destNotEmpty);
    }

    public static ConversationImportResult Import(HubPaths paths, Guid profileId, string sourceDir, bool force)
    {
        if (!OperatingSystem.IsWindows()) throw new UnsupportedCapabilityException("Conversation import requires Windows path semantics.");
        var source = ResolveSource(sourceDir);
        var files = CollectImportFiles(source);
        if (files.Count == 0)
            throw new HubException($"No conversation databases were found under {source}.", "import_source_empty");

        foreach (var file in files)
            if (!IsSqliteFile(file))
                throw new HubException($"Refusing to import '{Path.GetFileName(file)}': missing SQLite header.", "import_not_sqlite");

        var conversations = ConversationsDir(paths, profileId);
        var brain = BrainDir(paths, profileId);
        Directory.CreateDirectory(conversations);
        Directory.CreateDirectory(brain);

        string? backupDir = null;
        var existing = Directory.Exists(conversations)
            ? Directory.EnumerateFiles(conversations, "*.db").ToList()
            : [];
        if (existing.Count > 0)
        {
            if (!force) throw new HubException("Destination conversations are not empty. Pass --force to back them up and replace.", "import_destination_not_empty");
            backupDir = Path.Combine(Path.GetDirectoryName(conversations)!, $"conversations-backup-{DateTime.UtcNow:yyyyMMddHHmmss}");
            Directory.CreateDirectory(backupDir);
            foreach (var file in existing)
            {
                var name = Path.GetFileName(file);
                File.Move(file, Path.Combine(backupDir, name));
                var id = Path.GetFileNameWithoutExtension(name);
                var brainSrc = Path.Combine(brain, id);
                if (Directory.Exists(brainSrc))
                    Directory.Move(brainSrc, Path.Combine(backupDir, "brain-" + id));
            }
        }

        var staging = Path.Combine(Path.GetDirectoryName(conversations)!, $"import-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                File.Copy(file, Path.Combine(staging, name), overwrite: false);
                var id = Path.GetFileNameWithoutExtension(name);
                var brainSrc = Path.Combine(source, "brain", id);
                if (Directory.Exists(brainSrc))
                    CopyDirectory(brainSrc, Path.Combine(staging, "brain-" + id));
            }
            foreach (var file in Directory.EnumerateFiles(staging, "*.db"))
            {
                var name = Path.GetFileName(file);
                File.Move(file, Path.Combine(conversations, name), overwrite: true);
            }
            foreach (var dir in Directory.EnumerateDirectories(staging, "brain-*"))
            {
                var id = Path.GetFileName(dir)["brain-".Length..];
                var destBrain = Path.Combine(brain, id);
                if (Directory.Exists(destBrain)) Directory.Delete(destBrain, recursive: true);
                Directory.Move(dir, destBrain);
            }
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            // After --force emptied the destination, a failed staging must not
            // leave the profile without conversations: put the backup back.
            if (backupDir is not null)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(backupDir, "*.db"))
                        File.Move(file, Path.Combine(conversations, Path.GetFileName(file)), overwrite: true);
                    foreach (var dir in Directory.EnumerateDirectories(backupDir, "brain-*"))
                    {
                        var id = Path.GetFileName(dir)["brain-".Length..];
                        var destBrain = Path.Combine(brain, id);
                        if (Directory.Exists(destBrain)) Directory.Delete(destBrain, recursive: true);
                        Directory.Move(dir, destBrain);
                    }
                    Directory.Delete(backupDir, recursive: true);
                    backupDir = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        return new ConversationImportResult(files.Count, files.Sum(f => new FileInfo(f).Length), backupDir,
            files.Select(Path.GetFileName).Where(n => n is not null).Select(n => n!).ToList());
    }

    private static string ResolveSource(string sourceDir)
    {
        var full = Path.GetFullPath(sourceDir);
        if (!Directory.Exists(full))
            throw new HubException($"Import source does not exist: {full}", "import_source_missing");
        return full;
    }

    private static List<string> CollectImportFiles(string source)
    {
        var dir = Path.Combine(source, "conversations");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.db")
            .Where(f => new FileInfo(f).Length > 0)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsSqliteFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[SqliteHeader.Length];
            var read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            return read == SqliteHeader.Length && header.SequenceEqual(SqliteHeader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? TryReadTitle(string dbPath)
    {
        // Cheap best-effort: scan a bounded prefix of the file for a printable title-ish
        // blob without a SQLite dependency. Corrupt or unreadable files yield null.
        using var stream = File.OpenRead(dbPath);
        var buffer = new byte[Math.Min(stream.Length, 256 * 1024)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (read <= 0) return null;
        var text = Encoding.UTF8.GetString(buffer, 0, read);
        const string key = "title";
        var index = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var slice = text[Math.Min(index + key.Length, text.Length)..Math.Min(index + key.Length + 200, text.Length)];
        var start = slice.IndexOfAny(['"', '\'']);
        if (start < 0) return null;
        var quote = slice[start];
        var end = slice.IndexOf(quote, start + 1);
        if (end <= start + 1) return null;
        var title = slice[(start + 1)..end].Trim();
        return title.Length == 0 || title.Length > 120 ? null : title;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
