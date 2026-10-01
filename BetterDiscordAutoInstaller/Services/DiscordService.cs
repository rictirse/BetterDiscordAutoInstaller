using System.Diagnostics;
using System.Text.Json;
using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Services;

public class DiscordService
{
    private static readonly HttpClient HttpClient = new();
    private const string OfficialDiscordDownloadUrl = "https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64";

    static DiscordService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public string? GetLogFilePath(DiscordEdition edition)
    {
        return Path.Combine(PathUtils.AppData, edition.ToString().ToLower(), "logs", $"{edition}_updater_rCURRENT.log");
    }

    /// <summary>
    /// 打 Discord 官方 Manifest API 取得線上最新版本號
    /// </summary>
    /// <param name="edition"></param>
    /// <returns></returns>
    public async Task<string?> GetLatestRemoteVersionAsync(DiscordEdition edition)
    {
        try
        {
            // 將通道轉為低管字串 (stable, canary, ptb)
            string channel = edition switch
            {
                DiscordEdition.DiscordPTB => "ptb",
                DiscordEdition.DiscordCanary => "canary",
                _ => "stable"
            };

            string apiUrl = $"https://discord.com/api/updates/distributions/app/manifests/latest?channel={channel}&platform=win&arch=x64";

            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

            var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync();
            var manifest = JsonSerializer.Deserialize<DiscordManifest>(json);
            string? versionStr = manifest?.Full?.GetVersionString();

            if (!string.IsNullOrEmpty(versionStr))
            {
                return versionStr; // 例如回傳 "1.0.9260"
            }
        }
        catch
        {
            // 發生網路或解析錯誤時回傳 null
        }

        return null;
    }

    /// <summary>
    /// 比對本地與線上的 Discord 版本，判斷是否為最新版
    /// </summary>
    /// <param name="edition"></param>
    /// <param name="parentPath"></param>
    /// <returns></returns>
    public async Task<bool> IsDiscordUpToDateAsync(DiscordEdition edition, string parentPath)
    {
        try
        {
            // 取得本地最新 app-x.x.x 資料夾並去頭去尾（如 app-1.0.9180 -> 1.0.9180）
            string localCoreFolder = GetLatestInstalledDiscordFolder(parentPath);
            string localVersionStr = localCoreFolder.Replace("app-", "").Trim();

            // 取得線上最新版本
            string? remoteVersionStr = await GetLatestRemoteVersionAsync(edition);

            if (string.IsNullOrEmpty(remoteVersionStr))
            {
                Console.WriteLine($"[Warning] Failed to fetch remote version for {edition}. Skipping API check.");
                return false;
            }

            if (Version.TryParse(localVersionStr, out var localVer) &&
                Version.TryParse(remoteVersionStr, out var remoteVer))
            {
                Console.WriteLine($"Checking {edition} version -> Local: {localVer}, Remote: {remoteVer}");
                return localVer >= remoteVer;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking Discord version for {edition}: {ex.Message}");
        }

        return false;
    }

    public string GetLatestInstalledDiscordFolder(string parentPath)
    {
        if (!Directory.Exists(parentPath))
            throw new DirectoryNotFoundException($"Discord path not found: {parentPath}");

        var appDirs = Directory.GetDirectories(parentPath, "app-*")
                               .Select(Path.GetFileName)
                               .Where(name => name != null)
                               .ToList();

        if (!appDirs.Any())
            throw new DirectoryNotFoundException($"No 'app-*' folder found in: {parentPath}");

        appDirs.Sort();
        return appDirs.Last()!;
    }

    public bool IsDiscordRunning(DiscordEdition edition)
    {
        var procName = edition.ToString();
        return Process.GetProcessesByName(procName).Length > 0;
    }

    public void KillDiscord(DiscordEdition edition)
    {
        var procName = edition.ToString();
        foreach (var proc in Process.GetProcessesByName(procName))
        {
            try
            {
                Console.WriteLine($"Killing process: {proc.ProcessName} (PID: {proc.Id})");
                proc.Kill();
                proc.WaitForExit(3000);
            }
            catch { }
        }
    }

    public void StartDiscord(DiscordEdition edition, string parentPath)
    {
        var updateExe = Path.Combine(parentPath, "Update.exe");
        if (!File.Exists(updateExe))
            throw new FileNotFoundException($"Update.exe not found at: {updateExe}");

        var psi = new ProcessStartInfo
        {
            FileName = updateExe,
            Arguments = $"--processStart {edition.ToExecutableName()}",
            UseShellExecute = true,
            CreateNoWindow = true
        };
        Process.Start(psi);
    }

    public bool IsDiscordUpdating(DiscordEdition edition)
    {
        var logPath = GetLogFilePath(edition);
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return false;

        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var content = reader.ReadToEnd();

            string[] finishMessages = ["Updater main thread exiting", "Already up to date. Nothing to do"];
            foreach (var msg in finishMessages)
            {
                if (content.Contains(msg)) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 下載 Discord 官方安裝檔至 %temp% 並自動啟動安裝，且等待安裝完成
    /// </summary>
    /// <returns></returns>
    public async Task DownloadAndInstallDiscordAsync()
    {
        string tempPath = Path.GetTempPath();
        string installerPath = Path.Combine(tempPath, "DiscordSetup.exe");

        Console.WriteLine($"Downloading Discord installer to: {installerPath}");

        using (var response = await HttpClient.GetAsync(OfficialDiscordDownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalBytesRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalBytesRead += bytesRead;

                if (totalBytes > 0)
                {
                    double percentage = ((double)totalBytesRead / totalBytes) * 100;
                    double currentMb = totalBytesRead / 1024.0 / 1024.0;
                    double totalMb = totalBytes / 1024.0 / 1024.0;
                    Console.Write($"\rDownloading... {percentage:F1}% ({currentMb:F1}MB / {totalMb:F1}MB)");
                }
            }
        }

        Console.WriteLine("\nDownload complete. Starting Discord installer...");

        // 啟動安裝檔並等待安裝完成
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true
        };

        using var process = Process.Start(psi);
        if (process != null)
        {
            Console.WriteLine("Waiting for Discord installation to finish...");
            await process.WaitForExitAsync();
            Console.WriteLine("Discord installation finished.");
        }
    }
}