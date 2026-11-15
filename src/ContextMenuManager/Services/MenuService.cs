using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using ContextMenuManager.Models;
using Microsoft.Win32;

namespace ContextMenuManager.Services;

/// <summary>
/// Reads third-party context menu entries and turns them on/off using only HKCU.
/// Mechanics (proven on carbon, see scratch\proof):
///  - classic default: HKCU\Software\Classes\CLSID\{86ca1aa0-...}\InprocServer32 present = classic menu.
///  - shell extensions + packaged Win11 items: REG_SZ {CLSID} under HKCU ...\Shell Extensions\Blocked.
///  - static verbs: LegacyDisable under HKCU\Software\Classes\&lt;class&gt;\shell\&lt;verb&gt;.
/// Nothing in HKLM is ever written.
/// </summary>
public sealed class MenuService
{
    public const string ClassicClsid = "{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    public const string ClassicKey = @"Software\Classes\CLSID\" + ClassicClsid;
    public const string BlockedKey = @"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    private static readonly string[] VerbClasses = { "*", "AllFilesystemObjects", "Directory", "Directory\\Background", "Folder", "Drive" };
    private static readonly string[] ExtClasses = VerbClasses.Concat(new[] { "exefile", "lnkfile", "batfile", "cmdfile", "LibraryFolder", "CompressedFolder" }).ToArray();
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

    public AppState State { get; } = AppState.Load();

    // ---------------- classic menu ----------------

    public bool IsClassicMenuOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(ClassicKey + @"\InprocServer32");
        return k is not null;
    }

    public void SetClassicMenu(bool on)
    {
        if (on == IsClassicMenuOn()) return;
        Backup("classic-menu", new[] { ClassicKey });
        if (on)
        {
            EnsureKey(ClassicKey + @"\InprocServer32");
            using var k = Registry.CurrentUser.OpenSubKey(ClassicKey + @"\InprocServer32", true)!;
            k.SetValue("", "", RegistryValueKind.String);
            Record($"classic menu ON");
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(ClassicKey, false);
            State.CreatedKeys.RemoveAll(p => p.StartsWith(ClassicKey, StringComparison.OrdinalIgnoreCase));
            Record($"classic menu OFF");
        }
        State.Save();
    }

    // ---------------- scan ----------------

    public List<MenuEntry> Scan()
    {
        var hkcuBlocked = BlockedSet(Registry.CurrentUser);
        var hklmBlocked = BlockedSet(Registry.LocalMachine);
        var list = new List<MenuEntry>();
        try { list.AddRange(ScanPackaged(hkcuBlocked, hklmBlocked)); } catch (Exception ex) { Log.Write("packaged scan: " + ex); }
        try { list.AddRange(ScanShellExtensions(hkcuBlocked, hklmBlocked)); } catch (Exception ex) { Log.Write("shellex scan: " + ex); }
        try { list.AddRange(ScanVerbs()); } catch (Exception ex) { Log.Write("verb scan: " + ex); }
        return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static HashSet<string> BlockedSet(RegistryKey hive)
    {
        using var k = hive.OpenSubKey(BlockedKey);
        return new HashSet<string>(k?.GetValueNames().Select(NormGuid).Where(g => g is not null).Select(g => g!) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    private static string? NormGuid(string? s) => Guid.TryParse(s?.Trim(), out var g) ? g.ToString("B").ToUpperInvariant() : null;

    private static void ApplyBlockState(MenuEntry e, HashSet<string> hkcu, HashSet<string> hklm)
    {
        if (hklm.Contains(e.Clsid!)) e.LockedReason = "Disabled by system/admin";
        e.IsEnabled = !hkcu.Contains(e.Clsid!) && !hklm.Contains(e.Clsid!);
    }

    private IEnumerable<MenuEntry> ScanPackaged(HashSet<string> hkcu, HashSet<string> hklm)
    {
        using var root = Registry.ClassesRoot.OpenSubKey(@"PackagedCom\Package");
        if (root is null) yield break;
        foreach (var full in root.GetSubKeyNames())
        {
            var dir = Native.PackagePath(full);
            var manifest = dir is null ? null : Path.Combine(dir, "AppxManifest.xml");
            if (manifest is null || !File.Exists(manifest)) continue;
            XDocument doc;
            try { doc = XDocument.Load(manifest); } catch { continue; }
            var identity = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Identity");
            var publisher = identity?.Attribute("Publisher")?.Value ?? "";
            if (publisher.StartsWith("CN=Microsoft", StringComparison.OrdinalIgnoreCase)) continue;
            var pkgName = identity?.Attribute("Name")?.Value ?? full;

            var comClasses = new HashSet<string>(doc.Descendants()
                .Where(x => x.Name.LocalName == "Class" && x.Parent?.Name.LocalName is "ExeServer" or "SurrogateServer" or "ComServer")
                .Select(x => NormGuid(x.Attribute("Id")?.Value)).Where(g => g is not null).Select(g => g!), StringComparer.OrdinalIgnoreCase);

            var byClsid = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in doc.Descendants().Where(x => x.Name.LocalName == "Extension" && (string?)x.Attribute("Category") == "windows.fileExplorerContextMenus"))
                foreach (var item in ext.Descendants().Where(x => x.Name.LocalName == "ItemType"))
                    foreach (var verb in item.Elements().Where(x => x.Name.LocalName == "Verb"))
                    {
                        var g = NormGuid(verb.Attribute("Clsid")?.Value);
                        if (g is null || !comClasses.Contains(g)) continue;
                        if (!byClsid.TryGetValue(g, out var types)) byClsid[g] = types = new SortedSet<string>();
                        types.Add(ScopeLabel(item.Attribute("Type")?.Value ?? "*"));
                    }
            if (byClsid.Count == 0) continue;

            var display = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "Properties")?.Elements().FirstOrDefault(x => x.Name.LocalName == "DisplayName")?.Value ?? pkgName;
            if (display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            {
                var res = display.StartsWith("ms-resource://", StringComparison.OrdinalIgnoreCase) ? display
                    : display.StartsWith("ms-resource:/", StringComparison.OrdinalIgnoreCase) ? "ms-resource://" + pkgName + display["ms-resource:".Length..]
                    : "ms-resource://" + pkgName + "/Resources/" + display["ms-resource:".Length..];
                display = Native.LoadIndirect("@{" + full + "?" + res + "}") ?? pkgName;
            }
            var pubName = PublisherName(publisher);
            var logo = PackageLogo(doc);

            foreach (var (clsid, types) in byClsid)
            {
                var e = new MenuEntry
                {
                    Id = "pkg:" + clsid,
                    Kind = EntryKind.Packaged,
                    Name = display,
                    Source = $"{pubName} · {pkgName}",
                    Scopes = string.Join(", ", types),
                    Clsid = clsid,
                    Icon = dir is not null && logo is not null ? IconProtocol.FromPackageLogo(dir, logo) : IconSpec.None,
                };
                ApplyBlockState(e, hkcu, hklm);
                yield return e;
            }
        }
    }

    private static string PublisherName(string dn)
    {
        foreach (var part in dn.Split(','))
        {
            var p = part.Trim();
            if (p.StartsWith("O=", StringComparison.OrdinalIgnoreCase)) return p[2..].Trim('"');
        }
        return dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? dn.Split(',')[0][3..] : dn;
    }

    private IEnumerable<MenuEntry> ScanShellExtensions(HashSet<string> hkcu, HashSet<string> hklm)
    {
        var groups = new Dictionary<string, (string name, string dll, IconSpec icon, SortedSet<string> scopes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in ExtClasses)
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var k = hive.OpenSubKey($@"Software\Classes\{cls}\shellex\ContextMenuHandlers");
                if (k is null) continue;
                foreach (var name in k.GetSubKeyNames())
                {
                    using var h = k.OpenSubKey(name);
                    var clsid = NormGuid(h?.GetValue("") as string) ?? NormGuid(name);
                    if (clsid is null) continue;
                    if (!groups.TryGetValue(clsid, out var g))
                    {
                        var dll = ClsidDll(clsid);
                        if (string.IsNullOrEmpty(dll) || IsMicrosoftFile(dll)) { groups[clsid] = ("", "", IconSpec.None, new()); continue; }
                        string friendly = NormGuid(name) is null ? name : (ClsidName(clsid) ?? Path.GetFileNameWithoutExtension(dll));
                        groups[clsid] = g = (friendly, dll, ClsidIcon(clsid, dll), new SortedSet<string>());
                    }
                    g.scopes?.Add(ScopeLabel(cls));
                }
            }
        foreach (var (clsid, g) in groups)
        {
            if (g.dll.Length == 0) continue;
            var e = new MenuEntry
            {
                Id = "ext:" + clsid,
                Kind = EntryKind.ShellExtension,
                Name = g.name,
                Source = g.dll + (File.Exists(g.dll) ? "" : " (file not found)"),
                Scopes = string.Join(", ", g.scopes),
                Clsid = clsid,
                Icon = g.icon.IsEmpty ? IconProtocol.FromFile(g.dll) : g.icon,
            };
            ApplyBlockState(e, hkcu, hklm);
            yield return e;
        }
    }

    private static string? ClsidDll(string clsid)
    {
        using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
        var v = k?.GetValue("") as string;
        return string.IsNullOrWhiteSpace(v) ? null : Environment.ExpandEnvironmentVariables(v.Trim('"'));
    }


    private static IconSpec ClsidIcon(string clsid, string? dll)
    {
        using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\DefaultIcon");
        var spec = IconProtocol.FromIconValue(k?.GetValue("") as string);
        return spec.IsEmpty ? IconProtocol.FromFile(dll) : spec;
    }

    private static string? PackageLogo(XDocument doc)
    {
        var visual = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "VisualElements");
        var logo = (string?)visual?.Attribute("Square44x44Logo")
            ?? (string?)visual?.Attribute("Square150x150Logo")
            ?? (string?)visual?.Attribute("Logo");
        if (logo is not null) return logo;
        var tile = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "DefaultTile");
        return (string?)tile?.Attribute("Square44x44Logo")
            ?? (string?)tile?.Attribute("Square150x150Logo");
    }

    private static string? ClsidName(string clsid)
    {
        using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}");
        return k?.GetValue("") as string is { Length: > 0 } s ? s : null;
    }

    private static bool UnderWindows(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var p = Environment.ExpandEnvironmentVariables(path.Trim('"'));
        return Path.IsPathRooted(p) && p.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMicrosoftFile(string path)
    {
        if (UnderWindows(path)) return true;
        if (!Path.IsPathRooted(path)) return true; // bare system DLL name (e.g. "shell32.dll") resolves from System32
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path).CompanyName is { } c && c.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return false;
    }

    private sealed class VerbAcc
    {
        public string Name = "";
        public string Source = "";
        public IconSpec Icon = IconSpec.None;
        public SortedSet<string> Scopes = new();
        public List<VerbLocation> Locations = new();
        public bool AnyHkcuDisabled, AnyHklmDisabled;
    }

    private IEnumerable<MenuEntry> ScanVerbs()
    {
        var acc = new Dictionary<string, VerbAcc>(StringComparer.OrdinalIgnoreCase);
        foreach (var cls in VerbClasses)
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cu = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{cls}\shell")) if (cu is not null) names.UnionWith(cu.GetSubKeyNames());
            using (var lm = Registry.LocalMachine.OpenSubKey($@"Software\Classes\{cls}\shell")) if (lm is not null) names.UnionWith(lm.GetSubKeyNames());

            foreach (var verb in names)
            {
                var sub = $@"Software\Classes\{cls}\shell\{verb}";
                using var cuK = Registry.CurrentUser.OpenSubKey(sub);
                using var lmK = Registry.LocalMachine.OpenSubKey(sub);
                using var cuC = Registry.CurrentUser.OpenSubKey(sub + @"\command");
                using var lmC = Registry.LocalMachine.OpenSubKey(sub + @"\command");
                string? V(string n) => (cuK?.GetValue(n) as string) ?? (lmK?.GetValue(n) as string);
                bool Has(string n) => cuK?.GetValue(n) is not null || lmK?.GetValue(n) is not null;
                // A key we created only to disable an HKLM verb has no other content; read real data from HKLM.
                var rawName = V("MUIVerb") is { Length: > 0 } mv ? mv : V("");
                var cmd = (cuC?.GetValue("") as string) ?? (lmC?.GetValue("") as string);
                var delegateClsid = (cuC?.GetValue("DelegateExecute") as string) ?? (lmC?.GetValue("DelegateExecute") as string) ?? V("ExplorerCommandHandler");
                bool hasSub = Has("SubCommands") || Has("ExtendedSubCommandsKey")
                              || (cuK?.OpenSubKey("shell")?.SubKeyCount ?? 0) > 0 || (lmK?.OpenSubKey("shell")?.SubKeyCount ?? 0) > 0;

                if (Has("ProgrammaticAccessOnly")) continue;
                if (string.IsNullOrEmpty(rawName) && string.IsNullOrEmpty(cmd) && !hasSub) continue;
                if (IsMicrosoftVerb(rawName, cmd, delegateClsid)) continue;

                var display = ResolveName(rawName) ?? verb;
                var exe = ExeFromCommand(cmd) ?? ExeFromIcon(V("Icon"));
                var icon = IconProtocol.FromIconValue(V("Icon"));
                if (icon.IsEmpty) icon = IconProtocol.FromFile(exe);
                if (!acc.TryGetValue(verb, out var a)) acc[verb] = a = new VerbAcc { Name = display, Source = exe ?? $@"HKCR\{cls}\shell\{verb}", Icon = icon };
                if (a.Icon.IsEmpty && !icon.IsEmpty) a.Icon = icon;
                a.Scopes.Add(ScopeLabel(cls));
                a.Locations.Add(new VerbLocation(cls, verb, cuK is not null, lmK is not null));
                if (cuK?.GetValue("LegacyDisable") is not null) a.AnyHkcuDisabled = true;
                if (lmK?.GetValue("LegacyDisable") is not null) a.AnyHklmDisabled = true;
            }
        }
        foreach (var (verb, a) in acc)
        {
            var e = new MenuEntry
            {
                Id = "verb:" + verb,
                Kind = EntryKind.StaticVerb,
                Name = a.Name,
                Source = a.Source,
                Scopes = string.Join(", ", a.Scopes),
                Locations = a.Locations,
                Icon = a.Icon,
            };
            if (a.AnyHklmDisabled) e.LockedReason = "Disabled by system/admin";
            e.IsEnabled = !a.AnyHkcuDisabled && !a.AnyHklmDisabled;
            yield return e;
        }
    }

    private static bool IsMicrosoftVerb(string? rawName, string? cmd, string? delegateClsid)
    {
        if (rawName is { Length: > 1 } && rawName.StartsWith('@'))
        {
            var dll = rawName[1..].Split(',')[0];
            if (!dll.Contains('\\') || UnderWindows(dll)) return true;
        }
        var exe = ExeFromCommand(cmd);
        if (exe is not null && Path.IsPathRooted(exe) && UnderWindows(exe)) return true;
        if (NormGuid(delegateClsid) is { } g && ClsidDll(g) is { } d && IsMicrosoftFile(d)) return true;
        return false;
    }

    private static string? ResolveName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.StartsWith('@') ? Native.LoadIndirect(raw) ?? raw : raw;
        return s.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&").Trim();
    }

    internal static string? ExeFromCommand(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            return end > 1 ? cmd[1..end] : null;
        }
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (i > 0) return cmd[..(i + 4)];
        var sp = cmd.IndexOf(' ');
        return sp > 0 ? cmd[..sp] : cmd;
    }

    private static string? ExeFromIcon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        var s = icon.Trim();
        if (s.StartsWith('"')) { var end = s.IndexOf('"', 1); s = end > 1 ? s[1..end] : s.Trim('"'); }
        else { var c = s.LastIndexOf(','); if (c > 0 && int.TryParse(s[(c + 1)..], out _)) s = s[..c]; }
        return Environment.ExpandEnvironmentVariables(s);
    }

    private static string ScopeLabel(string cls) => cls switch
    {
        "*" => "Files",
        "AllFilesystemObjects" => "Files and folders",
        "Directory" or "Folder" => "Folders",
        "Directory\\Background" => "Folder background",
        "Drive" => "Drives",
        _ => cls,
    };

    // ---------------- toggling ----------------

    public void SetEnabled(MenuEntry e, bool on)
    {
        if (!e.CanToggle) throw new InvalidOperationException(e.LockedReason);
        var safe = new string(e.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (e.Kind is EntryKind.Packaged or EntryKind.ShellExtension)
        {
            Backup(safe, new[] { BlockedKey });
            if (!on)
            {
                EnsureKey(BlockedKey);
                using var k = Registry.CurrentUser.OpenSubKey(BlockedKey, true)!;
                if (k.GetValue(e.Clsid!) is null)
                {
                    k.SetValue(e.Clsid!, "", RegistryValueKind.String);
                    State.AddValue(BlockedKey, e.Clsid!);
                }
            }
            else
            {
                using (var k = Registry.CurrentUser.OpenSubKey(BlockedKey, true))
                {
                    if (k is not null)
                        foreach (var n in k.GetValueNames().Where(n => NormGuid(n) == e.Clsid)) k.DeleteValue(n, false);
                }
                State.RemoveValue(BlockedKey, e.Clsid!);
                CleanupCreated(BlockedKey);
            }
        }
        else
        {
            var keys = e.Locations.Select(l => $@"Software\Classes\{l.ClassName}\shell\{l.VerbKey}").ToArray();
            Backup(safe, keys);
            foreach (var key in keys)
            {
                if (!on)
                {
                    EnsureKey(key);
                    using var k = Registry.CurrentUser.OpenSubKey(key, true)!;
                    if (k.GetValue("LegacyDisable") is null)
                    {
                        k.SetValue("LegacyDisable", "", RegistryValueKind.String);
                        State.AddValue(key, "LegacyDisable");
                    }
                }
                else
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(key, true)) k?.DeleteValue("LegacyDisable", false);
                    State.RemoveValue(key, "LegacyDisable");
                    CleanupCreated(key);
                }
            }
        }
        e.IsEnabled = on;
        Record($"{(on ? "enabled" : "disabled")} {e.Id} ({e.Name})");
        State.Save();
    }

    /// <summary>Creates any missing HKCU keys along the path and records each one as ours.</summary>
    private void EnsureKey(string subPath)
    {
        var parts = subPath.Split('\\');
        var cur = "";
        foreach (var p in parts)
        {
            cur = cur.Length == 0 ? p : cur + "\\" + p;
            using var existing = Registry.CurrentUser.OpenSubKey(cur);
            if (existing is not null) continue;
            using var created = Registry.CurrentUser.CreateSubKey(cur);
            State.AddKey(cur);
        }
    }

    /// <summary>Deletes keys this app created (deepest first) once they are empty again.</summary>
    private void CleanupCreated(string subPath)
    {
        var cur = subPath;
        while (cur.Length > 0 && State.OwnsKey(cur))
        {
            using (var k = Registry.CurrentUser.OpenSubKey(cur))
            {
                if (k is not null && (k.ValueCount > 0 || k.SubKeyCount > 0)) break;
            }
            Registry.CurrentUser.DeleteSubKey(cur, false);
            State.RemoveKey(cur);
            var i = cur.LastIndexOf('\\');
            cur = i > 0 ? cur[..i] : "";
        }
    }

    private void Record(string msg)
    {
        State.History.Add($"{DateTime.Now:s} {msg}");
        if (State.History.Count > 200) State.History.RemoveRange(0, State.History.Count - 200);
        Log.Write(msg);
    }

    /// <summary>Exports each HKCU key (or notes it did not exist) into one .reg file under backups.</summary>
    public string Backup(string name, IEnumerable<string> hkcuSubKeys)
    {
        Directory.CreateDirectory(AppPaths.Backups);
        var file = Path.Combine(AppPaths.Backups, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{name}.reg");
        var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
        foreach (var sub in hkcuSubKeys)
        {
            var full = @"HKEY_CURRENT_USER\" + sub;
            using (var k = Registry.CurrentUser.OpenSubKey(sub))
            {
                if (k is null) { sb.Append($"; {full} did not exist before this change\r\n\r\n"); continue; }
            }
            var tmp = Path.Combine(Path.GetTempPath(), $"cmm-{Guid.NewGuid():N}.reg");
            try
            {
                var psi = new ProcessStartInfo("reg.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("export"); psi.ArgumentList.Add(full); psi.ArgumentList.Add(tmp); psi.ArgumentList.Add("/y");
                using var p = Process.Start(psi)!;
                p.WaitForExit(15000);
                if (p.ExitCode != 0 || !File.Exists(tmp)) throw new IOException($"reg export failed for {full}: {p.StandardError.ReadToEnd().Trim()}");
                var text = File.ReadAllText(tmp);
                var idx = text.IndexOf("\r\n", StringComparison.Ordinal);
                sb.Append(idx >= 0 ? text[(idx + 2)..].TrimStart('\r', '\n') : text).Append("\r\n");
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        File.WriteAllText(file, sb.ToString(), Encoding.Unicode);
        Log.Write("backup " + file);
        return file;
    }

    // ---------------- explorer ----------------

    public static async Task RestartExplorerAsync()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcessesByName("explorer").Where(p => p.SessionId == session))
        {
            try { p.Kill(); p.WaitForExit(5000); } catch (Exception ex) { Log.Write("kill explorer: " + ex.Message); }
        }
        // Winlogon normally restarts the shell itself; start it only if it didn't.
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            if (Process.GetProcessesByName("explorer").Any(p => p.SessionId == session)) { Log.Write("explorer restarted"); return; }
        }
        Process.Start(new ProcessStartInfo(Path.Combine(WinDir, "explorer.exe")) { UseShellExecute = true });
        Log.Write("explorer started manually");
    }
}
