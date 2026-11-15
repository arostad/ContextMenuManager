using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace ContextMenuManager.Services;

public sealed class UpdateInfo
{
    public required string RemoteVersion { get; init; }
    public required bool IsNewer { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// In-app updater following Andy's IPbind / Subtitle Compare pattern.
/// Version is read from the Worker first, then GitHub latest; the zip and .sha256 always come from GitHub.
/// </summary>
public static class UpdateChecker
{
    public const string NoReleaseMessage = "No published update was found.";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true,
            Proxy = System.Net.WebRequest.GetSystemWebProxy(),
        };
        handler.Proxy.Credentials = System.Net.CredentialCache.DefaultCredentials;
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        // workers.dev returns Cloudflare 1010 without a browser-like UA. Include the product token like IPbind.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ContextMenuManager/" + BuildInfo.Version);
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        client.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
        return client;
    }

    internal static HttpResponseMessage GetTrusted(string url)
    {
        var current = new Uri(UpdateProtocol.FreshUrl(url), UriKind.Absolute);
        for (var redirect = 0; ; redirect++)
        {
            UpdateProtocol.EnsureTrustedUri(current);
            var response = Http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (!UpdateProtocol.IsRedirect(response.StatusCode))
                return response;

            if (redirect >= UpdateProtocol.MaxRedirects)
            {
                response.Dispose();
                throw new HttpRequestException("The download used too many redirects.");
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw new HttpRequestException("The download redirect had no destination.");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }

    public static UpdateInfo Check()
    {
        if (TryReadVersion(UpdateProtocol.WorkerVersionUrl, out var remoteText, out var remote, out _)
            || TryReadVersion(UpdateProtocol.GitHubVersionUrl, out remoteText, out remote, out var error))
        {
            return new UpdateInfo
            {
                RemoteVersion = remoteText,
                IsNewer = remote > BuildInfo.Parsed,
            };
        }

        return new UpdateInfo { RemoteVersion = remoteText, IsNewer = false, Error = error };
    }

    private static bool TryReadVersion(string url, out string remoteText, out Version remote, out string error)
    {
        remoteText = "";
        remote = new Version(0, 0);
        error = "";
        try
        {
            using var response = GetTrusted(url);
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (UpdateProtocol.IsSoftVersionFailure(response.StatusCode, body))
            {
                error = NoReleaseMessage;
                return false;
            }
            response.EnsureSuccessStatusCode();
            if (!UpdateProtocol.TryParseVersionText(body, out remoteText, out remote))
            {
                error = "Latest version string was not readable.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = UpdateProtocol.DescribeError(ex, "Update check failed");
            return false;
        }
    }

    private static string ReadChecksum()
    {
        using var response = GetTrusted(UpdateProtocol.ChecksumUrl);
        response.EnsureSuccessStatusCode();
        using var input = response.Content.ReadAsStream();
        using var reader = new StreamReader(input, Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
        var buffer = new char[1025];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        if (count > 1024 || !UpdateProtocol.TryParseChecksum(new string(buffer, 0, count), out var hash))
            throw new InvalidOperationException("The update checksum was not readable.");
        return hash;
    }

    private static void DownloadArtifact(string destination)
    {
        using var response = GetTrusted(UpdateProtocol.ArtifactUrl);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > UpdateProtocol.MaxArtifactBytes)
            throw new InvalidOperationException("The update download was unexpectedly large.");

        using var input = response.Content.ReadAsStream();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > UpdateProtocol.MaxArtifactBytes)
                throw new InvalidOperationException("The update download was unexpectedly large.");
            output.Write(buffer, 0, read);
        }
    }

    private static (string Path, string Hash) DownloadVerifiedArtifact(string directory)
    {
        Directory.CreateDirectory(directory);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pending = Path.Combine(directory, $"{BuildInfo.ArtifactFileName}.{Guid.NewGuid():N}.new");
            try
            {
                var expectedHash = ReadChecksum();
                DownloadArtifact(pending);
                if (new FileInfo(pending).Length < UpdateProtocol.MinArtifactBytes)
                    throw new InvalidOperationException("Download failed.");
                if (string.Equals(UpdateProtocol.Sha256File(pending), expectedHash, StringComparison.OrdinalIgnoreCase))
                    return (pending, expectedHash);
                Log.Write("updater: hash mismatch, retrying download");
            }
            catch
            {
                try { File.Delete(pending); } catch { /* best effort */ }
                if (attempt == 1) throw;
                continue;
            }

            try { File.Delete(pending); } catch { /* best effort */ }
        }

        throw new InvalidOperationException("The downloaded update failed its integrity check.");
    }

    public static string ResolveInstalledExe()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && !LooksLikeSingleFileExtractPath(processPath))
            return Path.GetFullPath(processPath);

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, BuildInfo.ExeFileName));
    }

    internal static bool LooksLikeSingleFileExtractPath(string path)
    {
        var full = Path.GetFullPath(path);
        var temp = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(full, temp, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return full.Contains($"{Path.DirectorySeparatorChar}.net{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    public static void DownloadAndRestart()
    {
        var exe = ResolveInstalledExe();
        var dir = Path.GetDirectoryName(exe) ?? throw new InvalidOperationException("Could not resolve the install directory.");
        Directory.CreateDirectory(dir);
        var (pending, expectedHash) = DownloadVerifiedArtifact(dir);

        var pid = Environment.ProcessId;
        var errorFile = Path.Combine(dir, "update-error.txt");
        var script = Path.Combine(dir, $".ContextMenuManager-update-{Guid.NewGuid():N}.ps1");
        const string scriptText = """
            $ErrorActionPreference = "Stop"
            $pending = $env:CMM_UPDATE_PENDING
            $exe = $env:CMM_UPDATE_EXE
            $expectedHash = $env:CMM_UPDATE_SHA256
            $errorFile = $env:CMM_UPDATE_ERROR
            $minBytes = 1000000
            try {
                $process = Get-Process -Id ([int]$env:CMM_UPDATE_PID) -ErrorAction SilentlyContinue
                if ($process) { $process.WaitForExit() }
                if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $expectedHash) {
                    throw "The downloaded update failed its integrity check."
                }
                if ((Get-Item -LiteralPath $pending).Length -lt $minBytes) {
                    throw "The downloaded update was unexpectedly small."
                }
                $dest = Split-Path -Parent $exe
                $extract = Join-Path $dest (".cmm-update-" + [guid]::NewGuid().ToString("N"))
                New-Item -ItemType Directory -Path $extract | Out-Null
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                $zip = [System.IO.Compression.ZipFile]::OpenRead($pending)
                try {
                    $root = (Resolve-Path $extract).Path.TrimEnd('\') + '\'
                    foreach ($entry in $zip.Entries) {
                        $name = $entry.FullName
                        if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains([char]0)) {
                            throw "The update archive contained an unsafe path."
                        }
                        $combined = [IO.Path]::GetFullPath([IO.Path]::Combine($extract, $name.Replace('/', '\')))
                        if (-not $combined.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                            throw "The update archive contained an unsafe path."
                        }
                        if ($name.EndsWith('/') -or $name.EndsWith('\')) {
                            [IO.Directory]::CreateDirectory($combined) | Out-Null
                            continue
                        }
                        $dirName = [IO.Path]::GetDirectoryName($combined)
                        if ($dirName) { [IO.Directory]::CreateDirectory($dirName) | Out-Null }
                        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $combined, $true)
                    }
                } finally { $zip.Dispose() }
                $extractedExe = Join-Path $extract "ContextMenuManager.exe"
                if (-not (Test-Path -LiteralPath $extractedExe)) {
                    throw "The update archive did not contain ContextMenuManager.exe."
                }
                for ($attempt = 0; $attempt -lt 8; $attempt++) {
                    try {
                        Copy-Item -Path (Join-Path $extract '*') -Destination $dest -Recurse -Force
                        break
                    } catch {
                        if ($attempt -eq 7) { throw }
                        Start-Sleep -Seconds 1
                    }
                }
                if (-not (Test-Path -LiteralPath $exe)) {
                    throw "The updated executable is missing."
                }
                Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
                Start-Process -FilePath $exe
            } catch {
                "Update failed: $($_.Exception.Message)" | Set-Content -LiteralPath $errorFile
                if (Test-Path -LiteralPath $exe) {
                    Start-Process -FilePath $exe
                }
            } finally {
                Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
            }
            """;

        using (var stream = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            writer.Write(scriptText);

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.Environment["CMM_UPDATE_PENDING"] = pending;
        start.Environment["CMM_UPDATE_EXE"] = exe;
        start.Environment["CMM_UPDATE_SHA256"] = expectedHash.ToUpperInvariant();
        start.Environment["CMM_UPDATE_ERROR"] = errorFile;
        start.Environment["CMM_UPDATE_PID"] = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            if (Process.Start(start) is null)
                throw new InvalidOperationException("Could not start the update helper.");
        }
        catch
        {
            try { File.Delete(script); } catch { /* best effort */ }
            try { File.Delete(pending); } catch { /* best effort */ }
            throw;
        }
    }

}
