using System.Reflection;

namespace ContextMenuManager;

/// <summary>Single source for the version shown in About and compared by the updater.</summary>
public static class BuildInfo
{
    public const string AppName = "Context Menu Manager";
    public const string ExeFileName = "ContextMenuManager.exe";
    public const string ArtifactFileName = "ContextMenuManager.zip";

    public static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 1, 1);
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public static Version Parsed => System.Version.TryParse(Version, out var v) ? v : new Version(0, 1, 1);
}
