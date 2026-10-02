using System.Diagnostics;
using System.Text;

namespace AGProfileHub.Core;

internal static class NextDesktopLauncher
{
    // Execute the verified wrapper through its native Windows file association.
    // Explorer's path dispatcher can reject an existing default-root wrapper.
    public static void Launch(HubPaths paths, Guid id, LaunchPlan plan)
    {
        paths.EnsureProfileLayout(id);
        var wrapper = Path.Combine(paths.ProfileRoot(id), "launch-next.cmd");
        PathSafety.RejectExistingReparsePoints(paths.DataRoot, wrapper);
        File.WriteAllText(wrapper, Wrapper(plan), new UTF8Encoding(false));
        var info = new ProcessStartInfo(wrapper) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
        using var process = Process.Start(info) ?? throw new HubException("Desktop launch adapter failed.", "launch_failed");
    }
    internal static string Wrapper(LaunchPlan plan)
    {
        static string Escape(string value)
        {
            if (value.Contains('"') || value.Any(char.IsControl)) throw new ConfigurationException("Unsafe launch argument.");
            return value.Replace("%", "%%");
        }
        var lines = new List<string> { "@echo off", "@setlocal DisableDelayedExpansion", "@chcp 65001>nul" };
        foreach (var pair in plan.Environment)
        {
            if (pair.Key.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '_')) throw new ConfigurationException("Unsafe environment key.");
            lines.Add("set \"" + pair.Key + "=" + Escape(pair.Value) + "\"");
        }
        lines.Add("start \"\" \"" + Escape(plan.ExecutablePath) + "\" " + string.Join(" ", plan.Arguments.Select(arg => "\"" + Escape(arg) + "\"")));
        return string.Join("\r\n", lines) + "\r\n";
    }
}
