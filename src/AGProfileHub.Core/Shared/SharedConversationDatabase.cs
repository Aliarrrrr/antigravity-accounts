using System.Runtime.InteropServices;

namespace AGProfileHub.Core;

/// <summary>
/// Reads only the local App layouts inspected in 2.24.0. It never reads authentication files,
/// executes client code, merges conversation tables, or treats file validation as client restoration.
/// </summary>
internal static class SharedConversationDatabase
{
    private static readonly IReadOnlyDictionary<string, string[]> ConversationColumns = new Dictionary<string, string[]>
    {
        ["battle_mode_infos"] = ["idx", "data"],
        ["executor_metadata"] = ["idx", "data"],
        ["gen_metadata"] = ["idx", "data", "size"],
        ["parent_references"] = ["idx", "data"],
        ["steps"] = ["idx", "step_type", "status", "has_subtrajectory", "metadata", "error_details",
            "permissions", "task_details", "render_info", "step_payload", "step_format"],
        ["trajectory_meta"] = ["trajectory_id", "cascade_id", "trajectory_type", "source"],
        ["trajectory_metadata_blob"] = ["id", "data"]
    };

    public static string? ValidateConversation(string path, string expectedId)
    {
        if (!OperatingSystem.IsWindows()) return "shared_sqlite_unavailable";
        try
        {
            using var database = new ReadOnlyDatabase(path);
            if (database.Scalar("PRAGMA user_version") != "1" ||
                !database.Query("SELECT name FROM sqlite_schema WHERE type='table' AND name NOT GLOB 'sqlite_*' ORDER BY name")
                    .Select(row => row[0]).SequenceEqual(ConversationColumns.Keys.Order(StringComparer.Ordinal)))
                return "shared_conversation_format_unsupported";
            foreach (var table in ConversationColumns)
                if (!database.Columns(table.Key).SequenceEqual(table.Value)) return "shared_conversation_format_unsupported";
            if (database.Scalar("PRAGMA quick_check") != "ok") return "shared_conversation_database_invalid";
            var identities = database.Query("SELECT cascade_id, trajectory_id FROM trajectory_meta");
            if (identities.Count != 1 || identities[0][0] != expectedId || string.IsNullOrWhiteSpace(identities[0][1]))
                return "shared_conversation_id_mismatch";
            return null;
        }
        catch (DatabaseReadException) { return "shared_conversation_database_invalid"; }
        catch (IOException) { return "shared_import_source_busy"; }
        catch (UnauthorizedAccessException) { return "shared_import_source_unreadable"; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return "shared_sqlite_unavailable"; }
    }

    /// <summary>Null means the association index is unavailable or has an unconfirmed format.</summary>
    public static IReadOnlyList<string>? ReadProjectConversationIds(string indexPath, string projectId)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(indexPath)) return null;
        try
        {
            using var database = new ReadOnlyDatabase(indexPath);
            if (database.Scalar("PRAGMA user_version") != "3" ||
                database.Scalar("SELECT type FROM sqlite_schema WHERE name='conversation_summaries'") != "table") return null;
            var columns = database.Columns("conversation_summaries");
            if (!new[] { "conversation_id", "project_id", "parent_conversation_id", "raw_summary" }.All(columns.Contains)) return null;
            return database.Query("SELECT conversation_id FROM conversation_summaries WHERE project_id=? ORDER BY conversation_id", projectId)
                .Select(row => row[0]).ToArray();
        }
        catch (Exception error) when (error is DatabaseReadException or IOException or UnauthorizedAccessException or
            DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
    }

    private sealed class DatabaseReadException : Exception;

    private sealed class ReadOnlyDatabase : IDisposable
    {
        private readonly FileStream _guard;
        private nint _handle;
        public ReadOnlyDatabase(string path)
        {
            // This importer requires an offline database. Holding this handle excludes writers while
            // immutable=1 prevents SQLite from creating sidecars or changing source locking state.
            _guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                    if (File.Exists(path + suffix) && new FileInfo(path + suffix).Length > 0) throw new IOException();
                var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1";
                if (Native.sqlite3_open_v2(uri, out _handle, 1 | 0x40, 0) != 0) throw new DatabaseReadException();
                if (Native.sqlite3_busy_timeout(_handle, 1000) != 0) throw new DatabaseReadException();
            }
            catch { Dispose(); throw; }
        }

        public string? Scalar(string sql) => Query(sql).FirstOrDefault()?.FirstOrDefault();
        public string[] Columns(string table) => Query("PRAGMA table_info('" + table + "')").Select(row => row[1]).ToArray();
        public List<string[]> Query(string sql, string? parameter = null)
        {
            nint statement = 0;
            try
            {
                if (Native.sqlite3_prepare_v2(_handle, sql, -1, out statement, 0) != 0) throw new DatabaseReadException();
                if (parameter is not null && Native.sqlite3_bind_text(statement, 1, parameter, -1, new nint(-1)) != 0)
                    throw new DatabaseReadException();
                var rows = new List<string[]>();
                int result;
                while ((result = Native.sqlite3_step(statement)) == 100)
                {
                    var row = new string[Native.sqlite3_column_count(statement)];
                    for (var column = 0; column < row.Length; column++)
                        row[column] = Marshal.PtrToStringUTF8(Native.sqlite3_column_text(statement, column)) ?? "";
                    rows.Add(row);
                }
                if (result != 101) throw new DatabaseReadException();
                return rows;
            }
            finally { if (statement != 0) Native.sqlite3_finalize(statement); }
        }

        public void Dispose()
        {
            if (_handle != 0) { Native.sqlite3_close_v2(_handle); _handle = 0; }
            _guard.Dispose();
        }
    }

    private static class Native
    {
        private const string Library = "winsqlite3.dll";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out nint database, int flags, nint vfs);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_busy_timeout(nint database, int milliseconds);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_prepare_v2(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int length, out nint statement, nint tail);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_bind_text(nint statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int length, nint destructor);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(nint statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_count(nint statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern nint sqlite3_column_text(nint statement, int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(nint statement);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close_v2(nint database);
    }
}
