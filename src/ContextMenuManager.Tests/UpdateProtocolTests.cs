using System.Net;
using ContextMenuManager.Services;
using Xunit;

namespace ContextMenuManager.Tests;

public class UpdateProtocolTests
{
    [Theory]
    [InlineData("https://contextmenumanager-update-pings.dataspike.workers.dev/version.txt", true)]
    [InlineData("https://github.com/arostad/ContextMenuManager/releases/download/latest/version.txt", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/foo", true)]
    [InlineData("https://release-assets.githubusercontent.com/foo", true)]
    [InlineData("http://github.com/arostad/ContextMenuManager/releases/download/latest/version.txt", false)]
    [InlineData("https://evil.example/payload", false)]
    [InlineData("https://github.com.evil.example/payload", false)]
    [InlineData("https://user:pass@github.com/foo", false)]
    public void TrustedUriAllowlist(string url, bool trusted)
    {
        Assert.Equal(trusted, UpdateProtocol.IsTrustedUri(new Uri(url)));
    }

    [Fact]
    public void Checksum_AcceptsGnuSha256Sum()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.True(UpdateProtocol.TryParseChecksum(hash + "  ContextMenuManager.zip", out var parsed));
        Assert.Equal(hash, parsed);
        Assert.True(UpdateProtocol.TryParseChecksum(hash.ToUpperInvariant() + "  ContextMenuManager.zip\n", out parsed));
        Assert.Equal(hash, parsed);
        Assert.True(UpdateProtocol.TryParseChecksum(hash + "  ContextMenuManager.zip\r\n", out _));
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef ContextMenuManager.zip")] // one space
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde  ContextMenuManager.zip")] // 63 chars
    [InlineData("")]
    [InlineData("not-a-hash  ContextMenuManager.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  ContextMenuManager.zip\nextra")]
    public void Checksum_RejectsBadInput(string text)
    {
        Assert.False(UpdateProtocol.TryParseChecksum(text, out _));
    }

    [Fact]
    public void VersionText_ReadsFirstLine()
    {
        Assert.True(UpdateProtocol.TryParseVersionText("0.1.1\n", out var text, out var version));
        Assert.Equal("0.1.1", text);
        Assert.Equal(new Version(0, 1, 1), version);
        Assert.True(UpdateProtocol.TryParseVersionText("  1.2.3  \r\nignored", out text, out version));
        Assert.Equal("1.2.3", text);
        Assert.False(UpdateProtocol.TryParseVersionText("", out _, out _));
        Assert.False(UpdateProtocol.TryParseVersionText("not-a-version", out _, out _));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "", true)]
    [InlineData(HttpStatusCode.NotFound, "", true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, "nope", true)]
    [InlineData(HttpStatusCode.OK, "", true)]
    [InlineData(HttpStatusCode.OK, "0.1.1", false)]
    [InlineData(HttpStatusCode.Forbidden, "denied", false)]
    public void SoftVersionFailure(HttpStatusCode status, string body, bool soft)
    {
        Assert.Equal(soft, UpdateProtocol.IsSoftVersionFailure(status, body));
    }

    [Fact]
    public void ZipEntry_RejectsSlipAndAcceptsNormal()
    {
        var dest = Path.Combine(Path.GetTempPath(), "cmm-zip-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dest);
        try
        {
            Assert.True(UpdateProtocol.IsSafeZipEntry(dest, "ContextMenuManager.exe"));
            Assert.True(UpdateProtocol.IsSafeZipEntry(dest, "Assets/app.ico"));
            Assert.True(UpdateProtocol.IsSafeZipEntry(dest, "Assets/"));
            Assert.False(UpdateProtocol.IsSafeZipEntry(dest, "../evil.exe"));
            Assert.False(UpdateProtocol.IsSafeZipEntry(dest, "..\\evil.exe"));
            Assert.False(UpdateProtocol.IsSafeZipEntry(dest, "ok/../../evil.exe"));
            Assert.False(UpdateProtocol.IsSafeZipEntry(dest, ""));
        }
        finally { try { Directory.Delete(dest, true); } catch { } }
    }

    [Fact]
    public void Sha256File_HashesKnownBytes()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, "abc"u8.ToArray());
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", UpdateProtocol.Sha256File(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void FreshUrl_AddsCacheBustQuery()
    {
        var url = UpdateProtocol.FreshUrl("https://github.com/arostad/ContextMenuManager/releases/download/latest/version.txt");
        Assert.Contains("?t=", url, StringComparison.Ordinal);
        var again = UpdateProtocol.FreshUrl("https://example.invalid/path?x=1");
        Assert.Contains("&t=", again, StringComparison.Ordinal);
    }
}

public class IconProtocolTests
{
    [Theory]
    [InlineData(@"C:\Program Files\7-Zip\7-zip.dll,0", @"C:\Program Files\7-Zip\7-zip.dll", 0)]
    [InlineData(@"""C:\Program Files\PeaZip\peazip.exe"",-1", @"C:\Program Files\PeaZip\peazip.exe", -1)]
    [InlineData(@"%SystemRoot%\system32\imageres.dll,-5322", null, -5322)] // path expands
    [InlineData(@"C:\Apps\tool.exe", @"C:\Apps\tool.exe", 0)]
    public void ParseIconValue(string raw, string? expectedPath, int expectedIndex)
    {
        Assert.True(IconProtocol.TryParseIconValue(raw, out var path, out var index));
        Assert.Equal(expectedIndex, index);
        if (expectedPath is not null) Assert.Equal(expectedPath, path);
        else Assert.False(string.IsNullOrWhiteSpace(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("%1")]
    [InlineData("*")]
    public void ParseIconValue_RejectsEmpty(string raw)
    {
        Assert.False(IconProtocol.TryParseIconValue(raw, out _, out _));
    }

    [Fact]
    public void LogoScore_PrefersMatchingScaleAndTheme()
    {
        var dark200 = IconProtocol.ScoreLogoCandidate("Square44x44Logo.scale-200_theme-dark.png", 175, dark: true, 44);
        var light100 = IconProtocol.ScoreLogoCandidate("Square44x44Logo.scale-100_theme-light.png", 175, dark: true, 44);
        var plain = IconProtocol.ScoreLogoCandidate("Square44x44Logo.png", 175, dark: true, 44);
        Assert.True(dark200 > light100);
        Assert.True(dark200 > plain);
    }

    [Fact]
    public void PickPackageLogo_SelectsBestVariant()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cmm-logo-" + Guid.NewGuid().ToString("N"));
        var assets = Path.Combine(dir, "Assets");
        Directory.CreateDirectory(assets);
        try
        {
            File.WriteAllText(Path.Combine(assets, "Square44x44Logo.png"), "default");
            File.WriteAllText(Path.Combine(assets, "Square44x44Logo.scale-100.png"), "100");
            File.WriteAllText(Path.Combine(assets, "Square44x44Logo.scale-200.png"), "200");
            var picked = IconProtocol.PickPackageLogo(dir, @"Assets\Square44x44Logo.png", 200, dark: false, 44);
            Assert.NotNull(picked);
            Assert.Equal("Square44x44Logo.scale-200.png", Path.GetFileName(picked));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
