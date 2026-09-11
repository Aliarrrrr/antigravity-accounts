using System.Reflection;

namespace AGProfileHub.Core;

/// <summary>The build version comes only from Directory.Build.props.</summary>
public static class HubVersion
{
    public static string InformationalVersion { get; } = typeof(HubVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static string Version => InformationalVersion.Split('+')[0];
}
