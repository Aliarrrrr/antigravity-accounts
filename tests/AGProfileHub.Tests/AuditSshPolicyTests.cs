using System.Text.Json;
using AGProfileHub.Core;

internal static class AuditSshPolicyTests
{
    public static IEnumerable<(string Name, Func<Task> Run)> Cases =>
    [
        ("SSH policy missing manifest never creates host bridges", () => DefaultOffAsync(null)),
        ("SSH policy missing enabled field never creates host bridges", () => DefaultOffAsync("{\"schemaVersion\":1,\"managedLinks\":{}}")),
        ("SSH policy missing enabled field revokes only recorded owned items", MissingEnabledRevokesAsync),
        ("SSH policy malformed manifests fail closed without replacement", MalformedAsync),
        ("SSH policy explicit on survives reload and off preserves host targets", ExplicitOnAsync),
        ("SSH policy missing ownership record preserves unmanaged items", UnmanagedAsync),
        ("SSH policy migration defaults off and preserves existing explicit on", MigrationAsync),
        ("SSH policy Core creation persists off before any explicit enable", CoreCreationAsync)
    ];

    private static void Check(bool condition, string message) => TestAssert.Check(condition, message);
    private static string Manifest(HubPaths paths, Guid id) => Path.Combine(paths.ProfileRoot(id), "bridge.json");
    private static async Task<string> HostAsync(string root)
    {
        var host = Path.Combine(root, "synthetic-host");
        Directory.CreateDirectory(Path.Combine(host, ".ssh"));
        await File.WriteAllTextAsync(Path.Combine(host, ".gitconfig"), "[user]\n\tname = Synthetic SSH Policy Test\n");
        await File.WriteAllTextAsync(Path.Combine(host, ".ssh", "sentinel"), "SYNTHETIC-NO-PRIVATE-KEY");
        return host;
    }
    private static void NoLinks(HubPaths paths, Guid id) => Check(
        !File.Exists(Path.Combine(paths.Home(id), ".gitconfig")) &&
        !Directory.Exists(Path.Combine(paths.Home(id), ".ssh")) &&
        new DirectoryInfo(Path.Combine(paths.Home(id), ".ssh")).LinkTarget is null,
        "An implicit policy exposed the synthetic host Git/SSH items.");

    private static async Task DefaultOffAsync(string? manifest)
    {
        using var owned = new OwnedTestDirectory(prefix: "agprofile-ssh-policy-");
        var host = await HostAsync(owned.Root); var paths = new HubPaths(Path.Combine(owned.Root, "next"));
        var id = Guid.NewGuid(); paths.EnsureProfileLayout(id); var bridge = new NextBridge(paths);
        if (manifest is not null) await File.WriteAllTextAsync(Manifest(paths, id), manifest);
        try
        {
            // Exercise the same read/replay operation used by Launch and Restart, with a synthetic host.
            await bridge.ApplyAsync(id, (await bridge.ReadAsync(id)).Enabled, host);
            NoLinks(paths, id);
            Check(!(await bridge.ReadAsync(id)).Enabled, "Default off was not persisted.");
        }
        finally { await bridge.ApplyAsync(id, false, host); }
    }

    private static async Task MissingEnabledRevokesAsync()
    {
        using var owned = new OwnedTestDirectory(prefix: "agprofile-ssh-policy-");
        var host = await HostAsync(owned.Root); var paths = new HubPaths(Path.Combine(owned.Root, "next"));
        var id = Guid.NewGuid(); var bridge = new NextBridge(paths);
        try
        {
            var enabled = await bridge.ApplyAsync(id, true, host);
            await File.WriteAllTextAsync(Manifest(paths, id), JsonSerializer.Serialize(new { schemaVersion = 1, managedLinks = enabled.ManagedLinks }));
            await bridge.ApplyAsync(id, (await bridge.ReadAsync(id)).Enabled, host);
            NoLinks(paths, id);
            Check(File.Exists(Path.Combine(host, ".ssh", "sentinel")), "Revocation removed a host target.");
        }
        finally { await bridge.ApplyAsync(id, false, host); }
    }

    private static async Task MalformedAsync()
    {
        using var owned = new OwnedTestDirectory(prefix: "agprofile-ssh-policy-");
        var host = await HostAsync(owned.Root); var paths = new HubPaths(Path.Combine(owned.Root, "next"));
        var id = Guid.NewGuid(); paths.EnsureProfileLayout(id); var bridge = new NextBridge(paths);
        foreach (var (input, code) in new[]
        {
            ("{broken", "configuration_corrupt"),
            ("{\"schemaVersion\":1,\"enabled\":\"on\"}", "configuration_corrupt"),
            ("{\"schemaVersion\":2,\"enabled\":true}", "future_schema"),
            ("{\"enabled\":true}", "schema_missing"),
            ("{\"schemaVersion\":1,\"enabled\":true,\"managedLinks\":null}", "configuration_invalid"),
            ("{\"schemaVersion\":1,\"enabled\":true,\"managedLinks\":{\".gemini\":\"C:\\\\synthetic\"}}", "configuration_invalid"),
            ("{\"schemaVersion\":1,\"enabled\":true,\"managedLinks\":{\".ssh\":null}}", "configuration_invalid"),
            ("{\"schemaVersion\":1,\"enabled\":true,\"managedLinks\":{\".ssh\":\"relative\"}}", "configuration_invalid")
        })
        {
            await File.WriteAllTextAsync(Manifest(paths, id), input);
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => bridge.ReadAsync(id), code);
            await TestAssert.ThrowsCodeAsync<ConfigurationException>(() => bridge.ApplyAsync(id, true, host), code);
            Check(await File.ReadAllTextAsync(Manifest(paths, id)) == input, "Malformed bridge policy was replaced.");
            NoLinks(paths, id);
        }
    }

    private static async Task ExplicitOnAsync()
    {
        using var owned = new OwnedTestDirectory(prefix: "agprofile-ssh-policy-");
        var host = await HostAsync(owned.Root); var paths = new HubPaths(Path.Combine(owned.Root, "next"));
        var id = Guid.NewGuid(); var bridge = new NextBridge(paths);
        var git = await File.ReadAllTextAsync(Path.Combine(host, ".gitconfig"));
        try
        {
            await bridge.ApplyAsync(id, true, host); bridge = new NextBridge(paths);
            Check((await bridge.ReadAsync(id)).Enabled, "An explicit existing on policy was disabled.");
            await bridge.ApplyAsync(id, (await bridge.ReadAsync(id)).Enabled, host);
            Check(new DirectoryInfo(Path.Combine(paths.Home(id), ".ssh")).LinkTarget is { } target && NextHubService.EqualPath(target, Path.Combine(host, ".ssh")), "Explicit on did not retain the SSH junction.");
            await bridge.ApplyAsync(id, false, host); NoLinks(paths, id);
            Check(!(await bridge.ReadAsync(id)).Enabled, "Explicit off was not preserved.");
            Check(await File.ReadAllTextAsync(Path.Combine(host, ".gitconfig")) == git &&
                await File.ReadAllTextAsync(Path.Combine(host, ".ssh", "sentinel")) == "SYNTHETIC-NO-PRIVATE-KEY", "Host targets changed.");
        }
        finally { await bridge.ApplyAsync(id, false, host); }
    }

    private static async Task UnmanagedAsync()
    {
        using var owned = new OwnedTestDirectory(prefix: "agprofile-ssh-policy-");
        var host = await HostAsync(owned.Root); var paths = new HubPaths(Path.Combine(owned.Root, "next"));
        var id = Guid.NewGuid(); paths.EnsureProfileLayout(id); var bridge = new NextBridge(paths);
        var git = Path.Combine(paths.Home(id), ".gitconfig"); await File.WriteAllTextAsync(git, "USER-OWNED-SYNTHETIC");
        await bridge.ApplyAsync(id, (await bridge.ReadAsync(id)).Enabled, host);
        Check(await File.ReadAllTextAsync(git) == "USER-OWNED-SYNTHETIC", "Missing ownership policy removed an unmanaged item.");
        await TestAssert.ThrowsCodeAsync<HubException>(() => bridge.ApplyAsync(id, true, host), "bridge_conflict");
        Check(await File.ReadAllTextAsync(git) == "USER-OWNED-SYNTHETIC", "Explicit on replaced an unmanaged item.");
    }

    private static async Task MigrationAsync()
    {
        using var owned = new OwnedTestDirectory(prefix: "agprofile-ssh-policy-"); var host = await HostAsync(owned.Root);
        var source = new NextHubService(Path.Combine(owned.Root, "source"));
        var a = await source.Profiles.CreateAsync("Imported", null, IsolationBackendKind.ProcessLocal);
        var b = await source.Profiles.CreateAsync("Existing", null, IsolationBackendKind.ProcessLocal);
        var next = new NextHubService(Path.Combine(owned.Root, "destination"));
        next.Paths.EnsureProfileLayout(b.Id);
        await next.Repository.UpdateProfilesAsync(document => (document with { Profiles = [b] }, true));
        var registry = await File.ReadAllTextAsync(source.Paths.ProfilesFile);
        try
        {
            await next.Bridge.ApplyAsync(b.Id, true, host);
            await TestAssert.ThrowsCodeAsync<HubException>(() => NextMigration.RunAsync(next, source.Paths.DataRoot, false), "migration_preview_required");
            await NextMigration.RunAsync(next, source.Paths.DataRoot, true);
            Check((await next.Profiles.ListAsync()).Count == 1, "Preview changed destination metadata.");
            await NextMigration.RunAsync(next, source.Paths.DataRoot, false);
            Check(!(await next.Bridge.ReadAsync(a.Id)).Enabled, "New migration implicitly enabled host sharing.");
            NoLinks(next.Paths, a.Id);
            Check((await next.Bridge.ReadAsync(b.Id)).Enabled, "Migration changed an existing explicit on policy.");
            Check(await File.ReadAllTextAsync(source.Paths.ProfilesFile) == registry, "Migration changed its source.");
            await File.AppendAllTextAsync(source.Paths.ProfilesFile, " ");
            await TestAssert.ThrowsCodeAsync<HubException>(() => NextMigration.RunAsync(next, source.Paths.DataRoot, false), "migration_preview_required");
        }
        finally { await next.Bridge.ApplyAsync(b.Id, false, host); }
    }

    private static async Task CoreCreationAsync()
    {
        // Run only after the default-off fix; the historical CreateAsync would link the real host.
        using var owned = new OwnedTestDirectory(prefix: "agprofile-ssh-policy-");
        var hub = new NextHubService(Path.Combine(owned.Root, "next"));
        var profile = await hub.CreateAsync("Default off");
        Check(File.Exists(Manifest(hub.Paths, profile.Id)), "Creation did not persist an explicit bridge policy.");
        Check(!(await hub.Bridge.ReadAsync(profile.Id)).Enabled, "Core creation persisted on.");
        NoLinks(hub.Paths, profile.Id);
    }
}
