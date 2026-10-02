using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AGProfileHub.Core;

public static class NextConfiguration
{
    private static readonly JsonDocumentOptions Jsonc = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly HashSet<string> Numeric = ["editor.fontSize", "editor.tabSize", "editor.lineHeight", "editor.letterSpacing", "window.zoomLevel"];
    private static readonly HashSet<string> Boolean = ["editor.insertSpaces", "editor.detectIndentation", "editor.minimap.enabled",
        "editor.formatOnSave", "files.trimTrailingWhitespace", "files.insertFinalNewline", "files.trimFinalNewlines"];
    private static readonly Dictionary<string, string[]> Enums = new()
    {
        ["editor.wordWrap"] = ["off", "on", "wordWrapColumn", "bounded"],
        ["editor.renderWhitespace"] = ["none", "boundary", "selection", "trailing", "all"],
        ["files.autoSave"] = ["off", "afterDelay", "onFocusChange", "onWindowChange"],
        ["editor.cursorStyle"] = ["line", "block", "underline", "line-thin", "block-outline", "underline-thin"],
        ["editor.lineNumbers"] = ["off", "on", "relative", "interval"]
    };

    public static bool AllowedSetting(string name, JsonElement value) =>
        Numeric.Contains(name) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n) && Math.Abs(n) <= 10000 ||
        Boolean.Contains(name) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ||
        Enums.TryGetValue(name, out var choices) && value.ValueKind == JsonValueKind.String && choices.Contains(value.GetString());

    public static async Task<ConfigPreview> InheritAsync(HubPaths paths, Guid id, string sourceUserDirectory, bool apply, CancellationToken token = default)
    {
        await using var held = await FileLock.AcquireAsync(Path.Combine(paths.LocksRoot, "inherit-" + id.ToString("D") + ".lock"), TimeSpan.FromSeconds(30), token);
        sourceUserDirectory = Path.GetFullPath(sourceUserDirectory);
        PathSafety.RejectExistingPathAncestors(sourceUserDirectory);
        var source = Path.Combine(sourceUserDirectory, "settings.json");
        var destination = Path.Combine(paths.UserData(id), "User", "settings.json");
        PathSafety.RejectExistingReparsePoints(paths.DataRoot, destination);
        var allowed = new List<string>(); var skipped = new List<string>(); var conflicts = new List<string>();
        var additions = new Dictionary<string, JsonElement>();
        var original = File.Exists(destination) ? await File.ReadAllTextAsync(destination, token) : "{}";
        using var current = JsonDocument.Parse(original, Jsonc);
        if (current.RootElement.ValueKind != JsonValueKind.Object) throw new ConfigurationException("Destination settings must be an object; preserved.");
        if (File.Exists(source))
        {
            using var input = JsonDocument.Parse(await File.ReadAllTextAsync(source, token), Jsonc);
            if (input.RootElement.ValueKind != JsonValueKind.Object) throw new ConfigurationException("Source settings must be an object; preserved.");
            var seen = new HashSet<string>();
            foreach (var property in input.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ConfigurationException("Duplicate setting keys; no inheritance performed.");
                if (!AllowedSetting(property.Name, property.Value)) { skipped.Add(property.Name); continue; }
                if (current.RootElement.TryGetProperty(property.Name, out _)) { conflicts.Add(property.Name); continue; }
                allowed.Add(property.Name); additions[property.Name] = property.Value.Clone();
            }
        }
        // Keybinding command arguments and snippet bodies are arbitrary user text; do not guess that they contain no secrets.
        foreach (var item in new[] { "keybindings.json", "snippets" })
            if (File.Exists(Path.Combine(sourceUserDirectory, item)) || Directory.Exists(Path.Combine(sourceUserDirectory, item)))
                skipped.Add(item + ": arbitrary text requires individual review; not copied");
        skipped.Add("MCP, plugins, extensions and .gemini are outside the inheritance scope");
        if (apply && additions.Count > 0)
        {
            var serialized = JsonSerializer.Serialize(additions, new JsonSerializerOptions { WriteIndented = true });
            // Preserve destination JSONC including comments. Insert before the final object brace;
            // add the separator at the end of the last value, before any trailing comments.
            var close = StripComments(original).LastIndexOf('}');
            var prefix = original[..close];
            if (current.RootElement.EnumerateObject().Any())
            {
                var clean = StripComments(prefix);
                var last = clean.Length - 1;
                while (last >= 0 && char.IsWhiteSpace(clean[last])) last--;
                if (last >= 0 && clean[last] != ',') prefix = prefix.Insert(last + 1, ",");
            }
            var merged = prefix + "\n" + serialized[1..serialized.LastIndexOf('}')] + "\n" + original[close..];
            using var verify = JsonDocument.Parse(merged, Jsonc);
            await WriteTextAtomicAsync(paths.DataRoot, destination, merged, token);
        }
        return new ConfigPreview(source, destination, allowed, skipped, conflicts);
    }

    // Replace comment characters with spaces so insertion indices retain their original offsets.
    internal static string StripComments(string text)
    {
        var chars = text.ToCharArray(); bool quoted = false, escaped = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (quoted) { if (escaped) escaped = false; else if (chars[i] == '\\') escaped = true; else if (chars[i] == '"') quoted = false; continue; }
            if (chars[i] == '"') { quoted = true; continue; }
            if (i + 1 >= chars.Length || chars[i] != '/') continue;
            if (chars[i + 1] == '/') { while (i < chars.Length && chars[i] != '\n') chars[i++] = ' '; i--; }
            else if (chars[i + 1] == '*')
            {
                chars[i++] = ' '; chars[i++] = ' ';
                while (i + 1 < chars.Length && !(chars[i] == '*' && chars[i + 1] == '/')) chars[i++] = ' ';
                if (i + 1 < chars.Length) { chars[i] = ' '; chars[++i] = ' '; }
            }
        }
        return new string(chars);
    }

    internal static async Task WriteTextAtomicAsync(string managedRoot, string path, string text, CancellationToken token)
    {
        PathSafety.EnsureContained(managedRoot, path);
        PathSafety.RejectExistingReparsePoints(managedRoot, path);
        PathSafety.RejectExistingReparsePoints(managedRoot, path + ".bak");
        PathSafety.EnsureSafeDirectory(managedRoot, Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { var bytes = Encoding.UTF8.GetBytes(text); await stream.WriteAsync(bytes, token); stream.Flush(true); }
            PathSafety.RejectExistingReparsePoints(managedRoot, path);
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
