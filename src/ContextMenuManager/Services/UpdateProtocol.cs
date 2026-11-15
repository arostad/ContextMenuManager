using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ContextMenuManager.Services;

/// <summary>Pure updater rules shared with the unit tests: allowlist, checksum, version, zip paths.</summary>
public static class UpdateProtocol
{
    public const int MaxRedirects = 5;
    public const long MinArtifactBytes = 1_000_000;
    public const long MaxArtifactBytes = 500L * 1024 * 1024;

    public const string WorkerHost = "contextmenumanager-update-pings.dataspike.workers.dev";
    public const string WorkerVersionUrl = "https://" + WorkerHost + "/version.txt";
    public const string GitHubVersionUrl = "https://github.com/arostad/ContextMenuManager/releases/download/latest/version.txt";
    public const string ReleaseBaseUrl = "https://github.com/arostad/ContextMenuManager/releases/download/latest/";
    public const string ArtifactUrl = ReleaseBaseUrl + "ContextMenuManager.zip";
    public const string ChecksumUrl = ArtifactUrl + ".sha256";

    // Installed mode: the per-user Inno Setup installer is the update artifact, so the files, the Apps list entry
    // (DisplayVersion, EstimatedSize) and the uninstaller are all refreshed by the same silent run.
    public const string SetupFileName = "ContextMenuManagerSetup.exe";
    public const string SetupUrl = ReleaseBaseUrl + SetupFileName;
    public const string SetupChecksumUrl = SetupUrl + ".sha256";

    /// <summary>Inno Setup AppId. Must match installer\ContextMenuManager.iss. Never change it.</summary>
    public const string AppId = "{5F37C578-C736-4332-9161-A7E9C9EC6C78}";
    public const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";
    public const string RunningMutexName = "ContextMenuManager.Running";

    public static readonly HashSet<string> TrustedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        WorkerHost,
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
    };

    // GNU sha256sum: "<hash>  <filename>" (lowercase hex, two spaces), optional trailing newline.
    private static readonly Regex ChecksumPattern = new(
        @"\A(?<hash>[0-9a-fA-F]{64})  \S+[ \t]*(?:\r?\n)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsTrustedUri(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && TrustedHosts.Contains(uri.IdnHost)
        && string.IsNullOrEmpty(uri.UserInfo);

    public static void EnsureTrustedUri(Uri uri)
    {
        if (!IsTrustedUri(uri))
            throw new HttpRequestException("The download was redirected to an untrusted location.");
    }

    public static bool TryParseChecksum(string? text, out string hash)
    {
        hash = "";
        if (string.IsNullOrEmpty(text)) return false;
        var match = ChecksumPattern.Match(text);
        if (!match.Success) return false;
        hash = match.Groups["hash"].Value.ToLowerInvariant();
        return true;
    }

    public static bool TryParseVersionText(string? body, out string remoteText, out Version remote)
    {
        remoteText = "";
        remote = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(body)) return false;
        remoteText = body.Trim().Split('\n', '\r')[0].Trim();
        return Version.TryParse(remoteText, out remote!);
    }

    public static string FreshUrl(string url)
    {
        var sep = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{sep}t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    public static bool IsRedirect(HttpStatusCode status)
    {
        var code = (int)status;
        return code is 301 or 302 or 303 or 307 or 308;
    }

    /// <summary>No release yet (empty Worker 502, 404, empty body) should be silent, not an error banner.</summary>
    public static bool IsSoftVersionFailure(HttpStatusCode status, string? body)
    {
        var code = (int)status;
        if (code is 404 or 502 or 503) return true;
        return code is >= 200 and < 300 && string.IsNullOrWhiteSpace(body);
    }

    public static bool IsSafeZipEntry(string destinationDirectory, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return false;
        if (entryName.Contains('\0', StringComparison.Ordinal)) return false;
        var destRoot = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var relative = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (relative.StartsWith(Path.DirectorySeparatorChar)) return false;
        var combined = Path.GetFullPath(Path.Combine(destinationDirectory, relative));
        if (relative.EndsWith(Path.DirectorySeparatorChar))
            combined = combined.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return combined.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Installed mode means the HKCU Uninstall entry points at the folder the app runs from.
    /// A portable copy anywhere else stays portable even when an installed copy exists.
    /// </summary>
    public static bool IsSameDirectory(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            static string Norm(string p) => Path.GetFullPath(p.Trim().Trim('"'))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static string Sha256File(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    public static string DescribeError(Exception exception, string prefix = "Update failed")
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Net.Sockets.SocketException socket)
            {
                var detail = socket.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused
                    ? "connection refused"
                    : "socket " + socket.SocketErrorCode;
                return string.IsNullOrEmpty(socket.Message) ? $"{prefix} ({detail})." : $"{prefix} ({detail}): {socket.Message}";
            }
        }
        var msg = MostSpecificMessage(exception);
        return string.IsNullOrEmpty(msg) ? prefix + "." : prefix + ": " + msg;
    }

    private static string MostSpecificMessage(Exception exception)
    {
        var current = exception;
        string? msg = current.Message;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
            if (!string.IsNullOrWhiteSpace(current.Message)) msg = current.Message;
        }
        return msg ?? "";
    }
}
