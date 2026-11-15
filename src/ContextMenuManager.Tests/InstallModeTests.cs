using ContextMenuManager.Services;
using Xunit;

namespace ContextMenuManager.Tests;

public class InstallModeTests
{
    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\ContextMenuManager", @"C:\Users\a\AppData\Local\Programs\ContextMenuManager", true)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\ContextMenuManager\", @"C:\Users\a\AppData\Local\Programs\ContextMenuManager", true)]
    [InlineData(@"c:\users\A\appdata\local\programs\contextmenumanager", @"C:\Users\a\AppData\Local\Programs\ContextMenuManager\", true)]
    [InlineData("\"C:\\Users\\a\\AppData\\Local\\Programs\\ContextMenuManager\"", @"C:\Users\a\AppData\Local\Programs\ContextMenuManager", true)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\ContextMenuManager", @"C:\Users\a\Downloads\ContextMenuManager", false)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\ContextMenuManager", @"C:\Users\a\AppData\Local\Programs\ContextMenuManager2", false)]
    [InlineData(null, @"C:\Users\a\Downloads\ContextMenuManager", false)]
    [InlineData("", @"C:\Users\a\Downloads\ContextMenuManager", false)]
    [InlineData(@"C:\Users\a\AppData\Local\Programs\ContextMenuManager", null, false)]
    public void InstalledOnlyWhenUninstallEntryPointsAtThisFolder(string? installLocation, string? appFolder, bool installed)
    {
        Assert.Equal(installed, UpdateProtocol.IsSameDirectory(installLocation, appFolder));
    }

    [Fact]
    public void InstallerAssetsComeFromTrustedGitHubLatest()
    {
        Assert.True(UpdateProtocol.IsTrustedUri(new Uri(UpdateProtocol.SetupUrl)));
        Assert.True(UpdateProtocol.IsTrustedUri(new Uri(UpdateProtocol.SetupChecksumUrl)));
        Assert.Equal("https://github.com/arostad/ContextMenuManager/releases/download/latest/ContextMenuManagerSetup.exe", UpdateProtocol.SetupUrl);
        Assert.Equal(UpdateProtocol.SetupUrl + ".sha256", UpdateProtocol.SetupChecksumUrl);
        Assert.StartsWith("github.com", new Uri(UpdateProtocol.SetupUrl).Host);
    }

    [Fact]
    public void UninstallKeyIsTheInnoSetupKeyForTheAppId()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{5F37C578-C736-4332-9161-A7E9C9EC6C78}_is1", UpdateProtocol.UninstallSubKey);
    }

    [Fact]
    public void InstallerScriptMatchesTheAppsIdentifiers()
    {
        var iss = File.ReadAllText(FindRepoFile(Path.Combine("installer", "ContextMenuManager.iss")));
        // Inno escapes a leading brace by doubling it.
        Assert.Contains("AppId={" + UpdateProtocol.AppId, iss);
        Assert.Contains("AppMutex=" + UpdateProtocol.RunningMutexName, iss);
        Assert.Contains("OutputBaseFilename=" + Path.GetFileNameWithoutExtension(UpdateProtocol.SetupFileName), iss);
        Assert.Contains("PrivilegesRequired=lowest", iss);
        Assert.Contains(@"DefaultDirName={userpf}\ContextMenuManager", iss);
    }

    [Fact]
    public void UninstallerNeverDeletesTheUserDataFolder()
    {
        var iss = File.ReadAllText(FindRepoFile(Path.Combine("installer", "ContextMenuManager.iss")));
        var uninstallDelete = iss[iss.IndexOf("[UninstallDelete]", StringComparison.Ordinal)..iss.IndexOf("[Code]", StringComparison.Ordinal)];
        Assert.DoesNotContain("{localappdata}", uninstallDelete, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backups", uninstallDelete, StringComparison.OrdinalIgnoreCase);
        foreach (var line in uninstallDelete.Split('\n').Where(l => l.StartsWith("Type:", StringComparison.Ordinal)))
            Assert.Contains("{app}", line);
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }
}