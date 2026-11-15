namespace ContextMenuManager.Services;

public enum IconOrigin { None, RegistryIcon, TargetFile, PackageLogo }

public readonly record struct IconSpec(string Path, int Index, IconOrigin Origin, string? PackageLogoBase = null)
{
    public static IconSpec None => new("", 0, IconOrigin.None, null);
    public bool IsEmpty => Origin == IconOrigin.None || string.IsNullOrWhiteSpace(Path);
}

/// <summary>Parsing and package-logo scoring, kept free of WinUI so the unit tests can compile it alone.</summary>
public static class IconProtocol
{
    public static bool TryParseIconValue(string? raw, out string path, out int index)
    {
        path = "";
        index = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (s.StartsWith('@')) s = s[1..];
        if (s.Length == 0) return false;
        if (s is "%" or "%1" or "*") return false;

        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 1) return false;
            path = s[1..end].Trim();
            var rest = s[(end + 1)..].Trim();
            if (rest.StartsWith(',') && int.TryParse(rest[1..].Trim(), out var quotedIndex))
                index = quotedIndex;
            return path.Length > 0;
        }

        var comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out var parsedIndex))
        {
            path = s[..comma].Trim().Trim('"');
            index = parsedIndex;
            return path.Length > 0;
        }

        path = s.Trim().Trim('"');
        return path.Length > 0;
    }

    public static IconSpec FromIconValue(string? raw) =>
        TryParseIconValue(raw, out var path, out var index)
            ? new IconSpec(path, index, IconOrigin.RegistryIcon)
            : IconSpec.None;

    public static IconSpec FromFile(string? path, int index = 0) =>
        string.IsNullOrWhiteSpace(path) ? IconSpec.None : new IconSpec(path, index, IconOrigin.TargetFile);

    public static IconSpec FromPackageLogo(string packageDirectory, string logoRelative) =>
        new(packageDirectory, 0, IconOrigin.PackageLogo, logoRelative);

    /// <summary>Score a package logo variant. Higher is better. Qualifiers follow the Windows resource convention.</summary>
    public static int ScoreLogoCandidate(string fileName, int scalePercent, bool dark, int targetSizePx)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var quals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Patterns: name.scale-200.theme-dark.png or name.scale-200_theme-dark.png or contrast-white_altform-unplated
        foreach (var token in SplitQualifiers(name))
        {
            var dash = token.IndexOf('-');
            if (dash > 0) quals[token[..dash]] = token[(dash + 1)..];
            else quals[token] = "";
        }

        var score = 0;
        if (quals.TryGetValue("scale", out var scaleText) && int.TryParse(scaleText, out var scale))
            score += 1000 - Math.Min(1000, Math.Abs(scale - scalePercent));
        else if (quals.TryGetValue("targetsize", out var sizeText) && int.TryParse(sizeText, out var size))
            score += 800 - Math.Min(800, Math.Abs(size - targetSizePx) * 8);
        else
            score += 400; // unqualified default asset, typically scale-100

        if (quals.TryGetValue("theme", out var theme))
            score += string.Equals(theme, dark ? "dark" : "light", StringComparison.OrdinalIgnoreCase) ? 120
                : string.Equals(theme, dark ? "light" : "dark", StringComparison.OrdinalIgnoreCase) ? -80
                : 0;

        if (quals.ContainsKey("altform") && quals["altform"].Contains("unplated", StringComparison.OrdinalIgnoreCase))
            score += 40;

        if (quals.TryGetValue("contrast", out var contrast)
            && contrast.Equals("high", StringComparison.OrdinalIgnoreCase))
            score -= 30;

        return score;
    }

    public static string? PickPackageLogo(string packageDirectory, string logoRelative, int scalePercent, bool dark, int targetSizePx)
    {
        if (string.IsNullOrWhiteSpace(packageDirectory) || string.IsNullOrWhiteSpace(logoRelative)) return null;
        var relative = logoRelative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var declared = Path.GetFullPath(Path.Combine(packageDirectory, relative));
        var dir = Path.GetDirectoryName(declared);
        if (dir is null || !Directory.Exists(dir)) return File.Exists(declared) ? declared : null;

        var baseName = Path.GetFileNameWithoutExtension(declared);
        var ext = Path.GetExtension(declared);
        var candidates = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(baseName + "_", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(baseName + "-", StringComparison.OrdinalIgnoreCase))
                {
                    if (ext.Length == 0 || Path.GetExtension(file).Equals(ext, StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase)
                        || Path.GetExtension(file).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(file);
                }
            }
        }
        catch { /* unreadable package folder */ }

        if (candidates.Count == 0) return File.Exists(declared) ? declared : null;
        return candidates
            .OrderByDescending(f => ScoreLogoCandidate(f, scalePercent, dark, targetSizePx))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    internal static IEnumerable<string> SplitQualifiers(string stem)
    {
        var baseEnd = stem.Length;
        // Walk trailing .qual or _qual segments after the first dot following the logical name.
        // Example: Square44x44Logo.scale-200_theme-dark  or  Square44x44Logo.targetsize-44_altform-unplated
        var firstDot = -1;
        for (var i = 0; i < stem.Length; i++)
        {
            if (stem[i] == '.') { firstDot = i; break; }
        }
        if (firstDot < 0)
        {
            yield break;
        }
        var rest = stem[(firstDot + 1)..];
        foreach (var part in rest.Split(new[] { '.', '_' }, StringSplitOptions.RemoveEmptyEntries))
            yield return part;
    }
}
