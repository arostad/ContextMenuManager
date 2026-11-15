namespace ContextMenuManager.Services;

public static class Log
{
    private static readonly object Gate = new();
    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Root);
                File.AppendAllText(Path.Combine(AppPaths.Root, "app.log"), $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

public static class AppPaths
{
    public static string Root { get; } = Environment.GetEnvironmentVariable("CMM_DATA_DIR") is { Length: > 0 } d
        ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ContextMenuManager");
    public static string Backups => Path.Combine(Root, "backups");
    public static string State => Path.Combine(Root, "state.json");
    public static string Settings => Path.Combine(Root, "settings.json");
}
