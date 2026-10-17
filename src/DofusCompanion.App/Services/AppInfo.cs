using System.Reflection;

namespace DofusCompanion.App.Services;

/// <summary>Application name and version as shown in window titles (e.g. "Dofus Gold Boss - DGB 0.4.0").</summary>
public static class AppInfo
{
    /// <summary>Version without the build metadata suffix ("0.3.2+abc123" → "0.3.2").</summary>
    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
        .Split('+')[0];

    public const string Name = "Dofus Gold Boss - DGB";

    public static string Title { get; } = $"{Name} {Version}";
}
