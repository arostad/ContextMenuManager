namespace ContextMenuManager.Services;

// Shipping builds: no update test override. See UpdateTest\UpdateChecker.TestOverride.cs.
public static partial class UpdateChecker
{
    private static partial string? TestOverrideFile(string url) => null;
}
