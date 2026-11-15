using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ContextMenuManager.Models;

public enum EntryKind { StaticVerb, ShellExtension, Packaged }

/// <summary>One registry location a static verb lives in, e.g. ("Directory\Background", "git_gui").</summary>
public sealed record VerbLocation(string ClassName, string VerbKey, bool InHkcu, bool InHklm);

public sealed class MenuEntry : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required EntryKind Kind { get; init; }
    public required string Name { get; init; }
    public required string Source { get; init; }
    public required string Scopes { get; init; }
    public string? Clsid { get; init; }
    public List<VerbLocation> Locations { get; init; } = new();
    public string Glyph => Kind switch
    {
        EntryKind.Packaged => "\uE7B8",      // package
        EntryKind.ShellExtension => "\uE943", // code / extension
        _ => "\uE756",                        // command prompt
    };

    public bool ShowsInWin11Menu => Kind == EntryKind.Packaged;
    public bool ShowsInClassicMenu => true;
    public string WhereBadge => Kind == EntryKind.Packaged ? "Windows 11 menu + Show more options" : "Show more options";
    public string Secondary => string.IsNullOrEmpty(Scopes) ? Source : $"{Source}  ·  {Scopes}";

    /// <summary>Set when HKLM disables it; the user can't change it without admin.</summary>
    public string? LockedReason { get; set; }
    public bool CanToggle => LockedReason is null;
    public string StatusText => LockedReason ?? (IsEnabled ? "On" : "Off");
    public string OffText => LockedReason ?? "Off";

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (_isEnabled != value) { _isEnabled = value; OnChanged(); OnChanged(nameof(StatusText)); } }
    }

    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
