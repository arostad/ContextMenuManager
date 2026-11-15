using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Win32;

namespace ContextMenuManager.Services;

public sealed class UpdateInfo
{
    public required string RemoteVersion { get; init; }
    public required bool IsNewer { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// In-app updater following Andy's IPbind / Subtitle Compare pattern.
/// Version is read from the Worker first, then GitHub latest; artifacts and their .sha256 always come from GitHub.
/// Installed copies update by running the verified per-user installer silently (files, Apps-list version and
/// uninstaller all refresh together). Portable copies swap their folder from the verified zip.
/// </summary>
public static partial class UpdateChecker
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

    /// <summary>Release builds return null. Only builds made with -p:UpdateTestOverride=true read local files.</summary>
    private static partial string? TestOverrideFile(string url);

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
            string body;
            if (TestOverrideFile(url) is { } local)
            {
                body = File.Exists(local) ? File.ReadAllText(local) : "";
                if (string.IsNullOrWhiteSpace(body)) { error = NoReleaseMessage; return false; }
            }
            else
            {
                using var response = GetTrusted(url);
                body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (UpdateProtocol.IsSoftVersionFailure(response.StatusCode, body))
                {
                    error = NoReleaseMessage;
                    return false;
                }
                response.EnsureSuccessStatusCode();
            }
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

    private static string ReadChecksum(string url)
    {
        string text;
        if (TestOverrideFile(url) is { } local)
        {
            text = File.ReadAllText(local, Encoding.ASCII);
        }
        else
        {
            using var response = GetTrusted(url);
            response.EnsureSuccessStatusCode();
            using var input = response.Content.ReadAsStream();
            using var reader = new StreamReader(input, Encoding.ASCII, detectEncodingFromByteOrderMarks: false);
            var buffer = new char[1025];
            var count = reader.ReadBlock(buffer, 0, buffer.Length);
            if (count > 1024) throw new InvalidOperationException("The update checksum was not readable.");
            text = new string(buffer, 0, count);
        }
        if (text.Length > 1024 || !UpdateProtocol.TryParseChecksum(text, out var hash))
            throw new InvalidOperationException("The update checksum was not readable.");
        return hash;
    }

    private static void DownloadArtifact(string url, string destination)
    {
        if (TestOverrideFile(url) is { } local)
        {
            if (new FileInfo(local).Length > UpdateProtocol.MaxArtifactBytes)
                throw new InvalidOperationException("The update download was unexpectedly large.");
            File.Copy(local, destination, overwrite: false);
            return;
        }

        using var response = GetTrusted(url);
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

    /// <summary>Downloads an artifact, checks size bounds and SHA-256, and retries once on a mismatch.</summary>
    private static (string Path, string Hash) DownloadVerifiedArtifact(string url, string checksumUrl, string directory, Func<string> pendingName)
    {
        Directory.CreateDirectory(directory);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pending = Path.Combine(directory, pendingName());
            try
            {
                var expectedHash = ReadChecksum(checksumUrl);
                DownloadArtifact(url, pending);
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

    /// <summary>
    /// True when this copy was put here by the installer: the HKCU Uninstall entry's InstallLocation is the folder
    /// the app is running from. Portable copies never read or write the Uninstall entry beyond this check.
    /// </summary>
    public static bool IsInstalledCopy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UpdateProtocol.UninstallSubKey);
            var location = key?.GetValue("InstallLocation") as string;
            return UpdateProtocol.IsSameDirectory(location, Path.GetDirectoryName(ResolveInstalledExe()));
        }
        catch (Exception ex)
        {
            Log.Write("install mode: " + ex.Message);
            return false;
        }
    }

    public static void DownloadAndRestart()
    {
        var exe = ResolveInstalledExe();
        var dir = Path.GetDirectoryName(exe) ?? throw new InvalidOperationException("Could not resolve the install directory.");
        Directory.CreateDirectory(dir);
        var installed = IsInstalledCopy();

        string mode = "zip";
        string pending;
        string expectedHash;
        string? scriptDir = null;
        if (installed)
        {
            try
            {
                var updates = Path.Combine(AppPaths.Root, "updates");
                (pending, expectedHash) = DownloadVerifiedArtifact(
                    UpdateProtocol.SetupUrl, UpdateProtocol.SetupChecksumUrl, updates,
                    () => $"ContextMenuManagerSetup-{Guid.NewGuid():N}.exe");
                mode = "installer";
                scriptDir = updates;
            }
            catch (Exception ex)
            {
                // Older releases have no installer asset. The zip still updates the files and the Apps-list version.
                Log.Write("updater: installer not available, using the zip: " + UpdateProtocol.DescribeError(ex));
                (pending, expectedHash) = DownloadVerifiedArtifact(
                    UpdateProtocol.ArtifactUrl, UpdateProtocol.ChecksumUrl, dir,
                    () => $"{BuildInfo.ArtifactFileName}.{Guid.NewGuid():N}.new");
            }
        }
        else
        {
            (pending, expectedHash) = DownloadVerifiedArtifact(
                UpdateProtocol.ArtifactUrl, UpdateProtocol.ChecksumUrl, dir,
                () => $"{BuildInfo.ArtifactFileName}.{Guid.NewGuid():N}.new");
        }
        Log.Write($"updater: verified {mode} update, installed={installed}");

        var pid = Environment.ProcessId;
        var errorFile = Path.Combine(dir, "update-error.txt");
        var script = Path.Combine(scriptDir ?? dir, $".ContextMenuManager-update-{Guid.NewGuid():N}.ps1");
        const string scriptText = """
            $ErrorActionPreference = "Stop"
            $pending = $env:CMM_UPDATE_PENDING
            $exe = $env:CMM_UPDATE_EXE
            $expectedHash = $env:CMM_UPDATE_SHA256
            $errorFile = $env:CMM_UPDATE_ERROR
            $mode = $env:CMM_UPDATE_MODE
            $uninstallKey = $env:CMM_UPDATE_UNINSTALL_KEY
            $minBytes = 1000000
            $extract = $null
            function Sync-UninstallEntry([string]$dest) {
                # Installed copies only. Keeps the Apps list version and size in step with the files on disk.
                if ([string]::IsNullOrEmpty($uninstallKey) -or -not (Test-Path -LiteralPath $uninstallKey)) { return }
                $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
                if ($version) { Set-ItemProperty -LiteralPath $uninstallKey -Name DisplayVersion -Value $version.Trim() }
                $bytes = (Get-ChildItem -LiteralPath $dest -Recurse -File -Force | Measure-Object -Property Length -Sum).Sum
                $kb = [int][Math]::Ceiling($bytes / 1024)
                New-ItemProperty -LiteralPath $uninstallKey -Name EstimatedSize -PropertyType DWord -Value $kb -Force | Out-Null
            }
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
                if ($mode -eq "installer") {
                    $setupLog = [IO.Path]::ChangeExtension($pending, ".log")
                    $setupArgs = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/SP-", ('/LOG="' + $setupLog + '"'))
                    $setup = Start-Process -FilePath $pending -ArgumentList $setupArgs -Wait -PassThru
                    if ($setup.ExitCode -ne 0) { throw "The installer exited with code $($setup.ExitCode)." }
                    Remove-Item -LiteralPath $setupLog -Force -ErrorAction SilentlyContinue
                } else {
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
                    Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
                }
                if (-not (Test-Path -LiteralPath $exe)) {
                    throw "The updated executable is missing."
                }
                Sync-UninstallEntry $dest
                Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue
                Start-Process -FilePath $exe
            } catch {
                "Update failed: $($_.Exception.Message)" | Set-Content -LiteralPath $errorFile
                if ($extract) { Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue }
                Remove-Item -LiteralPath $pending -Force -ErrorAction SilentlyContinue
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
        start.Environment["CMM_UPDATE_MODE"] = mode;
        start.Environment["CMM_UPDATE_UNINSTALL_KEY"] = installed ? @"HKCU:\" + UpdateProtocol.UninstallSubKey : "";
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