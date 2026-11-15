namespace ContextMenuManager.Services;

// Local end-to-end update testing only. This file is compiled only when building with
// -p:UpdateTestOverride=true (build\package.ps1 -UpdateTestOverride). Release builds never contain it, so they can
// only update from the trusted hosts.
public static partial class UpdateChecker
{
    private static partial string? TestOverrideFile(string url)
    {
        var dir = Environment.GetEnvironmentVariable("CMM_UPDATE_TEST_DIR");
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var name = Uri.UnescapeDataString(new Uri(url).Segments[^1]);
        Log.Write("updater: TEST OVERRIDE " + url + " -> " + Path.Combine(dir, name));
        return Path.Combine(dir, name);
    }
}