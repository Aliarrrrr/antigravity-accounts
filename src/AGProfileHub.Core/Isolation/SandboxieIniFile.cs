using System.Text;

namespace AGProfileHub.Core;

/// <summary>
/// Reads and writes <c>Sandboxie.ini</c> using the exact encoding, newline style
/// and key spelling the Sandboxie driver expects.
/// </summary>
/// <remarks>
/// Sandboxie 1.18.2 / core 5.73.2 was observed to ignore indexed keys such as
/// <c>OpenFilePath.1=</c> while honouring repeated bare <c>OpenFilePath=</c>
/// keys. Indexed keys are still accepted on read so existing configuration is
/// understood, but writes always emit the bare form. Previously this parsing
/// existed twice (here and in scripts/Set-IsolationWriteThrough.ps1) and the two
/// copies disagreed, which produced configuration that looked applied but was
/// silently ignored by the driver.
/// </remarks>
public sealed class SandboxieIniFile
{
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;
    private readonly string _newline;
    private readonly bool _trailingNewline;
    private List<string> _lines;

    private SandboxieIniFile(string path, Encoding encoding, byte[] preamble, string newline,
        bool trailingNewline, List<string> lines)
    {
        Path = path;
        _encoding = encoding;
        _preamble = preamble;
        _newline = newline;
        _trailingNewline = trailingNewline;
        _lines = lines;
    }

    public string Path { get; }

    /// <summary>Path of the backup produced by the most recent <see cref="Save"/>.</summary>
    public string? LastBackupPath { get; private set; }

    /// <summary>Loads a configuration file. Throws when it does not exist.</summary>
    public static SandboxieIniFile Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ConfigurationException("A Sandboxie configuration path is required.", "sandbox_config_missing");
        if (!File.Exists(path))
            throw new HubException($"Sandboxie configuration '{path}' was not found.", "sandbox_config_missing");
        return Parse(path, File.ReadAllBytes(path));
    }

    public static SandboxieIniFile? TryLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return Parse(path, File.ReadAllBytes(path));
    }

    /// <summary>
    /// Opens the system configuration, preferring <c>%WINDIR%\Sandboxie.ini</c>
    /// and falling back to the Sandboxie installation directory.
    /// </summary>
    public static SandboxieIniFile? TryOpenSystem(string? sandboxieHome = null)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows))
        {
            var system = TryLoad(System.IO.Path.Combine(windows, "Sandboxie.ini"));
            if (system is not null) return system;
        }
        return string.IsNullOrWhiteSpace(sandboxieHome)
            ? null
            : TryLoad(System.IO.Path.Combine(sandboxieHome, "Sandboxie.ini"));
    }

    /// <summary>
    /// Values of <paramref name="key"/> inside <paramref name="box"/>, accepting
    /// both bare (<c>Key=</c>) and indexed (<c>Key.N=</c>) spellings.
    /// </summary>
    public IReadOnlyList<string> GetValues(string box, string key)
    {
        var values = new List<string>();
        var inside = false;
        var bare = key + "=";
        foreach (var raw in _lines)
        {
            var line = raw.Trim();
            if (IsSectionHeader(line, out var section))
            {
                inside = section.Equals(box, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inside) continue;
            if (line.StartsWith(bare, StringComparison.OrdinalIgnoreCase))
            {
                var value = line[bare.Length..].Trim();
                if (value.Length > 0) values.Add(value);
                continue;
            }
            if (line.Length > key.Length + 2 &&
                line.StartsWith(key, StringComparison.OrdinalIgnoreCase) &&
                line[key.Length] == '.' &&
                char.IsDigit(line[key.Length + 1]))
            {
                var separator = line.IndexOf('=');
                if (separator < 0) continue;
                var value = line[(separator + 1)..].Trim();
                if (value.Length > 0) values.Add(value);
            }
        }
        return values;
    }

    public bool ContainsValue(string box, string key, string value)
    {
        foreach (var candidate in GetValues(box, key))
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public bool SectionExists(string box)
    {
        foreach (var raw in _lines)
        {
            if (IsSectionHeader(raw.Trim(), out var section) &&
                section.Equals(box, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Replaces every bare and indexed occurrence of <paramref name="key"/> in
    /// <paramref name="box"/> with bare <c>Key=Value</c> lines. The change is
    /// in memory until <see cref="Save"/> is called.
    /// </summary>
    public void ReplaceValues(string box, string key, IReadOnlyList<string> values)
    {
        var output = new List<string>(_lines.Count + values.Count);
        var inside = false;
        var seen = false;
        foreach (var raw in _lines)
        {
            var line = raw.Trim();
            if (IsSectionHeader(line, out var section))
            {
                inside = section.Equals(box, StringComparison.OrdinalIgnoreCase);
                output.Add(raw);
                if (inside && !seen)
                {
                    foreach (var value in values) output.Add($"{key}={value}");
                    seen = true;
                }
                continue;
            }
            if (inside && OwnsKey(line, key)) continue;
            output.Add(raw);
        }
        if (!seen)
            throw new HubException($"Sandboxie configuration has no section '{box}'.", "sandbox_box_missing");
        _lines = output;
    }

    /// <summary>
    /// Writes the file atomically, keeping an adjacent backup, then re-reads it
    /// and fails when the saved content does not match what was requested.
    /// </summary>
    public void Save()
    {
        var body = string.Join(_newline, _lines);
        var text = _trailingNewline ? body + _newline : body;
        var payload = _encoding.GetBytes(text);
        var bytes = new byte[_preamble.Length + payload.Length];
        _preamble.CopyTo(bytes, 0);
        payload.CopyTo(bytes, _preamble.Length);

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var backup = $"{Path}.agph-{stamp}-{Guid.NewGuid().ToString("N")[..8]}.bak";
        var temp = $"{Path}.agph-{Guid.NewGuid().ToString("N")}.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Replace(temp, Path, backup, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HubException(
                $"Sandboxie configuration '{Path}' could not be written: {ex.Message} " +
                "This file is system-wide and needs elevation.",
                "sandbox_config_failed", ex);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
        LastBackupPath = backup;

        var saved = TryLoad(Path);
        if (saved is null || !string.Equals(string.Join(_newline, saved._lines), body, StringComparison.Ordinal))
            throw new HubException("Sandboxie configuration did not retain the saved rules.", "sandbox_config_failed");
        _lines = saved._lines;
    }

    private static bool OwnsKey(string line, string key)
    {
        if (line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return true;
        return line.Length > key.Length + 2 &&
               line.StartsWith(key, StringComparison.OrdinalIgnoreCase) &&
               line[key.Length] == '.' &&
               char.IsDigit(line[key.Length + 1]) &&
               line.Contains('=');
    }

    private static bool IsSectionHeader(string line, out string name)
    {
        if (line.Length > 2 && line.StartsWith('[') && line.EndsWith(']'))
        {
            name = line[1..^1];
            return true;
        }
        name = string.Empty;
        return false;
    }

    private static SandboxieIniFile Parse(string path, byte[] raw)
    {
        Encoding encoding;
        byte[] preamble;
        int offset;
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
            preamble = [0xFF, 0xFE];
            offset = 2;
        }
        else if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            preamble = [0xEF, 0xBB, 0xBF];
            offset = 3;
        }
        else
        {
            encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            preamble = [];
            offset = 0;
        }
        var text = encoding.GetString(raw, offset, raw.Length - offset);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = new List<string>(text.Split(newline, StringSplitOptions.None));
        var trailing = lines.Count > 0 && lines[^1].Length == 0;
        if (trailing) lines.RemoveAt(lines.Count - 1);
        return new SandboxieIniFile(path, encoding, preamble, newline, trailing, lines);
    }
}
