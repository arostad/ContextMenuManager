using System.Text.Json;

namespace ContextMenuManager.Services;

public sealed class CreatedValue
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>What this app created, so turning something back on removes only our own keys/values.</summary>
public sealed class AppState
{
    public List<string> CreatedKeys { get; set; } = new();
    public List<CreatedValue> CreatedValues { get; set; } = new();
    public List<string> History { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppState Load()
    {
        try { if (File.Exists(AppPaths.State)) return JsonSerializer.Deserialize<AppState>(File.ReadAllText(AppPaths.State)) ?? new(); }
        catch (Exception ex) { Log.Write("state load failed: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var tmp = AppPaths.State + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, AppPaths.State, true);
    }

    public bool OwnsKey(string key) => CreatedKeys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
    public void AddKey(string key) { if (!OwnsKey(key)) CreatedKeys.Add(key); }
    public void RemoveKey(string key) => CreatedKeys.RemoveAll(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
    public void AddValue(string key, string name)
    {
        if (!CreatedValues.Any(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            CreatedValues.Add(new CreatedValue { Key = key, Name = name });
    }
    public void RemoveValue(string key, string name) =>
        CreatedValues.RemoveAll(v => v.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed class AppSettings
{
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    public static AppSettings Load()
    {
        try { if (File.Exists(AppPaths.Settings)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new(); }
        catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.Settings, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
