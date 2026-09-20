using System.IO.Compression;
using System.Security.Cryptography;

namespace Birooni.Client.Updates;

public class AutoUpdateManager
{
    private readonly HttpClient _httpClient;

    public AutoUpdateManager(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    /// <summary>
    /// Downloads the update package from downloadUrl, verifies SHA-256 (if provided),
    /// and unpacks/stages the files in stagingDirectory.
    /// </summary>
    public async Task<bool> DownloadAndStageUpdateAsync(
        string downloadUrl,
        string stagingDirectory,
        string? expectedChecksumSha256 = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                try { Directory.Delete(stagingDirectory, true); } catch { /* Ignore */ }
            }
            Directory.CreateDirectory(stagingDirectory);

#if NET8_0_OR_GREATER
            byte[] packageBytes = await _httpClient.GetByteArrayAsync(downloadUrl, cancellationToken);
#else
            byte[] packageBytes = await _httpClient.GetByteArrayAsync(downloadUrl);
#endif

            if (!string.IsNullOrWhiteSpace(expectedChecksumSha256))
            {
                using var sha256 = SHA256.Create();
                byte[] hash = sha256.ComputeHash(packageBytes);
#if NET8_0_OR_GREATER
                string actualHash = Convert.ToHexString(hash);
#else
                string actualHash = BitConverter.ToString(hash).Replace("-", "");
#endif
                if (!string.Equals(actualHash, expectedChecksumSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return false; // Checksum mismatch
                }
            }

            // Detect zip format either by extension or PK header bytes
            bool isZip = downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                         (packageBytes.Length > 4 && packageBytes[0] == 0x50 && packageBytes[1] == 0x4B);

            if (isZip)
            {
                using var ms = new MemoryStream(packageBytes);
                using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
#if NET8_0_OR_GREATER
                archive.ExtractToDirectory(stagingDirectory, overwriteFiles: true);
#else
                foreach (var entry in archive.Entries)
                {
                    var dest = Path.Combine(stagingDirectory, entry.FullName);
                    var dir = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    if (!string.IsNullOrEmpty(entry.Name))
                        entry.ExtractToFile(dest, true);
                }
#endif
                return true;
            }

            // Fallback: If it's a standalone DLL or binary
            var uri = new Uri(downloadUrl);
            var fileName = Path.GetFileName(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "BiruBox.dll";

            var destPath = Path.Combine(stagingDirectory, fileName);
#if NET8_0_OR_GREATER
            await File.WriteAllBytesAsync(destPath, packageBytes, cancellationToken);
#else
            using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            {
                await fs.WriteAsync(packageBytes, 0, packageBytes.Length, cancellationToken);
            }
#endif
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches a hidden detached background process that waits for the Revit process to terminate,
    /// then replaces the plugin files with the staged files and cleans up.
    /// </summary>
    public bool ScheduleApplyOnExit(int hostProcessId, string stagingDirectory, string targetDirectory)
    {
        try
        {
            var cleanStaging = stagingDirectory.TrimEnd('\\', '/');
            var cleanTarget = targetDirectory.TrimEnd('\\', '/');

            // Escape single quotes for PowerShell
            cleanStaging = cleanStaging.Replace("'", "''");
            cleanTarget = cleanTarget.Replace("'", "''");

            var script = $"$p = Get-Process -Id {hostProcessId} -ErrorAction SilentlyContinue; " +
                         $"if ($p) {{ $p.WaitForExit() }}; " +
                         $"Start-Sleep -Milliseconds 600; " +
                         $"Copy-Item -Path '{cleanStaging}\\*' -Destination '{cleanTarget}' -Recurse -Force; " +
                         $"Remove-Item -Path '{cleanStaging}' -Recurse -Force -ErrorAction SilentlyContinue";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-WindowStyle Hidden -NoProfile -NonInteractive -Command \"& {{ {script} }}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
