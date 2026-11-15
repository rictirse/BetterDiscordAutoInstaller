using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Services;

public class DiscordService
{
    private static readonly HttpClient HttpClient = new();
    private const string OfficialDiscordDownloadUrl = "https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64";

    // Discord 新版 updater 的 log 是「累積」的（約 1MB 才輪替一次），會保留之前好幾次啟動的紀錄，
    // 所以判斷是否正在更新時只能看「本次啟動」那一段（最後一個 "Logging initialized" 之後）。
    private const string LogSessionStartMarker = "Logging initialized";
    private const string UpdateStartMarker = "Starting update to latest.";
    private const string UpdateCompleteMarker = "Update to latest complete.";
    private const string UpdateFailedMarker = "ERROR [updater_client]: Failed";
    private const string ModuleRequestMarker = "Requesting installation of module";
    private const string ModuleDoneMarker = "Install of module";
    private const string ModuleFailedMarker = "TaskFailed(ModuleInstall";

    // updater 在兩個動作之間最長會停約 3 秒（例如 CollectGarbage → InstallModule），留一點餘裕
    private static readonly TimeSpan LogQuietPeriod = TimeSpan.FromSeconds(8);

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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var response = await HttpClient.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return null;

            string json = await response.Content.ReadAsStringAsync(cts.Token);
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
    /// 比對本地與線上的 Discord 版本。
    /// true = 已是最新版；false = 有新版本；null = 無法判斷（例如開機時網路還沒好），呼叫端不應該因此去動 Discord。
    /// </summary>
    public async Task<bool?> IsDiscordUpToDateAsync(DiscordEdition edition, string parentPath)
    {
        try
        {
            // 取得本地最新 app-x.x.x 資料夾並去頭（如 app-1.0.9180 -> 1.0.9180）
            string localCoreFolder = GetLatestInstalledDiscordFolder(parentPath);

            // 取得線上最新版本
            string? remoteVersionStr = await GetLatestRemoteVersionAsync(edition);

            if (string.IsNullOrEmpty(remoteVersionStr))
            {
                Console.WriteLine($"[Warning] Failed to fetch remote version for {edition}. Skipping Discord version check.");
                return null;
            }

            var localVer = ParseAppFolderVersion(localCoreFolder);
            if (localVer != null && Version.TryParse(remoteVersionStr, out var remoteVer))
            {
                Console.WriteLine($"Checking {edition} version -> Local: {localVer}, Remote: {remoteVer}");
                return localVer >= remoteVer;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking Discord version for {edition}: {ex.Message}");
        }

        return null;
    }

    public string GetLatestInstalledDiscordFolder(string parentPath)
    {
        if (!Directory.Exists(parentPath))
            throw new DirectoryNotFoundException($"Discord path not found: {parentPath}");

        // 只會比對 "app-*"，不會抓到更新中的 ".app-x.x.x.installing"
        var appDirs = Directory.GetDirectories(parentPath, "app-*")
                               .Select(Path.GetFileName)
                               .OfType<string>()
                               .ToList();

        if (appDirs.Count == 0)
            throw new DirectoryNotFoundException($"No 'app-*' folder found in: {parentPath}");

        // 依版本號排序；字串排序會讓 app-1.0.10000 排在 app-1.0.9999 前面
        return appDirs
            .OrderBy(name => ParseAppFolderVersion(name) ?? new Version(0, 0))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Last();
    }

    public static Version? ParseAppFolderVersion(string folderName)
    {
        var versionPart = folderName.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ? folderName[4..] : folderName;
        return Version.TryParse(versionPart, out var version) ? version : null;
    }

    public bool IsDiscordRunning(DiscordEdition edition)
    {
        var procs = Process.GetProcessesByName(edition.ToString());
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    /// <summary>
    /// 結束所有 Discord process，並等到它們真的退出、檔案 handle 釋放後才回傳
    /// </summary>
    public async Task<bool> KillDiscordAsync(DiscordEdition edition, int timeoutMs = 10000)
    {
        foreach (var proc in Process.GetProcessesByName(edition.ToString()))
        {
            using (proc)
            {
                try
                {
                    Console.WriteLine($"Killing process: {proc.ProcessName} (PID: {proc.Id})");
                    proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // process 可能已經自己結束
                }
            }
        }

        var sw = Stopwatch.StartNew();
        while (IsDiscordRunning(edition))
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                Console.WriteLine($"[WARN] {edition} did not exit within {timeoutMs / 1000}s.");
                return false;
            }
            await Task.Delay(250);
        }

        // 給 Windows 一點時間釋放檔案 handle
        await Task.Delay(1000);
        return true;
    }

    /// <summary>
    /// 啟動 Discord。失敗時只回傳 false，不丟例外（避免整個流程中斷、Discord 就一直沒被開回來）
    /// </summary>
    public bool StartDiscord(DiscordEdition edition, string parentPath)
    {
        string exeName = edition.ToExecutableName();

        // 優先使用 Squirrel 的 Update.exe（與 Discord 開機自啟相同的方式）；
        // 新版安裝可能只剩根目錄的 launcher 或 app-x 底下的執行檔，依序 fallback。
        var candidates = new List<(string File, string Args)>();

        var updateExe = Path.Combine(parentPath, "Update.exe");
        if (File.Exists(updateExe)) candidates.Add((updateExe, $"--processStart {exeName}"));

        var rootLauncher = Path.Combine(parentPath, exeName);
        if (File.Exists(rootLauncher)) candidates.Add((rootLauncher, ""));

        try
        {
            var appExe = Path.Combine(parentPath, GetLatestInstalledDiscordFolder(parentPath), exeName);
            if (File.Exists(appExe)) candidates.Add((appExe, ""));
        }
        catch
        {
            // 沒有 app-* 資料夾時略過
        }

        foreach (var (file, args) in candidates)
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = args,
                    WorkingDirectory = Path.GetDirectoryName(file)!,
                    UseShellExecute = true
                });
                Console.WriteLine($"Started {edition}: {file} {args}".TrimEnd());
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to start {file}: {ex.Message}");
            }
        }

        Console.WriteLine($"[ERROR] Could not start {edition}: no launcher found in {parentPath}");
        return false;
    }

    /// <summary>
    /// 有開機自動啟動設定時，用 Discord 登錄的同一個指令啟動，保留「啟動時最小化」等設定；
    /// 沒有設定或指令不是指向這個安裝目錄時，改用一般方式啟動。
    /// </summary>
    public bool StartDiscord(DiscordEdition edition, string parentPath, DiscordAutoStartEntry? autoStart)
    {
        if (autoStart != null && IsInsideDirectory(autoStart.FileName, parentPath) && File.Exists(autoStart.FileName))
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo
                {
                    FileName = autoStart.FileName,
                    Arguments = autoStart.Arguments,
                    WorkingDirectory = Path.GetDirectoryName(autoStart.FileName)!,
                    UseShellExecute = true
                });
                Console.WriteLine($"Started {edition} with its Windows startup command{(autoStart.StartMinimized ? " (minimized)" : "")}.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to run {edition} startup command: {ex.Message}");
            }
        }

        return StartDiscord(edition, parentPath);
    }

    private static bool IsInsideDirectory(string file, string directory)
    {
        try
        {
            var fullFile = Path.GetFullPath(file);
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullFile.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> WaitForDiscordStartAsync(DiscordEdition edition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (IsDiscordRunning(edition)) return true;
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>
    /// 等待 Discord 內建 updater 閒置（沒有在下載 / 安裝 host 或 module）。
    /// 在 updater 工作中砍掉 Discord，會讓更新失敗甚至留下壞掉的安裝。
    /// 回傳 false 表示逾時仍在更新。
    /// </summary>
    public async Task<bool> WaitForUpdaterIdleAsync(DiscordEdition edition, string parentPath, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        string? lastReason = null;
        int idleStreak = 0;

        while (sw.Elapsed < timeout)
        {
            string? reason;
            try
            {
                reason = GetUpdaterBusyReason(edition, parentPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Could not read {edition} updater state: {ex.Message}");
                reason = null;
            }

            if (reason == null)
            {
                // 連續幾次都閒置才算數，避免剛好落在 updater 兩個步驟之間、或 Discord 更新後自我重啟的空檔
                if (++idleStreak >= 3) return true;
            }
            else
            {
                idleStreak = 0;
                if (reason != lastReason)
                {
                    Console.WriteLine($"[INFO] Waiting for {edition} updater ({reason})...");
                    lastReason = reason;
                }
            }

            await Task.Delay(1000);
        }

        Console.WriteLine($"[WARN] {edition} updater is still busy after {timeout.TotalSeconds:0}s.");
        return false;
    }

    /// <summary>
    /// 回傳 updater 忙碌的原因；閒置時回傳 null
    /// </summary>
    private string? GetUpdaterBusyReason(DiscordEdition edition, string parentPath)
    {
        string? logPath = GetLogFilePath(edition);
        bool logExists = !string.IsNullOrEmpty(logPath) && File.Exists(logPath);

        // log 還在持續寫入 → updater 正在工作（也涵蓋 Discord 更新完自我重啟的空檔）
        if (logExists && DateTime.Now - File.GetLastWriteTime(logPath!) < LogQuietPeriod)
            return "updater is active";

        if (!IsDiscordRunning(edition)) return null;

        // 新版 updater 會先把 host 下載到 ".app-x.x.x.installing"，完成後才改名成 "app-x.x.x"
        if (Directory.Exists(parentPath) && Directory.EnumerateDirectories(parentPath, ".app-*.installing").Any())
            return "downloading a new Discord version";

        DateTime? processStart = GetEarliestProcessStartTime(edition);

        if (!logExists)
        {
            // 沒有 log 可參考：至少讓 Discord 啟動一段時間再動它
            return processStart != null && DateTime.Now - processStart.Value < TimeSpan.FromSeconds(20) ? "starting up" : null;
        }

        return AnalyzeUpdaterLog(ReadLogTail(logPath!, 512 * 1024), processStart, DateTime.Now);
    }

    /// <summary>
    /// 分析 updater log 的最後一段（本次啟動），判斷 updater 是否還在工作。
    /// </summary>
    internal static string? AnalyzeUpdaterLog(string tail, DateTime? processStart, DateTime now)
    {
        bool recentlyStarted = processStart != null && now - processStart.Value < TimeSpan.FromMinutes(2);
        int sessionIdx = tail.LastIndexOf(LogSessionStartMarker, StringComparison.Ordinal);
        string session = sessionIdx >= 0 ? tail[sessionIdx..] : tail;

        // log 裡最後一次啟動早於目前的 Discord process → 這次啟動的 updater 還沒開始寫 log
        if (recentlyStarted && sessionIdx >= 0 &&
            TryParseLogLineTimestamp(tail, sessionIdx, out var sessionStart) &&
            sessionStart < processStart!.Value - TimeSpan.FromSeconds(15))
        {
            return "starting up";
        }

        int lastStart = session.LastIndexOf(UpdateStartMarker, StringComparison.Ordinal);
        int lastEnd = Math.Max(
            session.LastIndexOf(UpdateCompleteMarker, StringComparison.Ordinal),
            session.LastIndexOf(UpdateFailedMarker, StringComparison.Ordinal));
        if (lastStart > lastEnd) return "checking / installing updates";

        int requested = CountOccurrences(session, ModuleRequestMarker);
        int finished = CountOccurrences(session, ModuleDoneMarker) + CountOccurrences(session, ModuleFailedMarker);
        if (requested > finished) return "installing modules";

        return null;
    }

    private static DateTime? GetEarliestProcessStartTime(DiscordEdition edition)
    {
        DateTime? earliest = null;
        foreach (var p in Process.GetProcessesByName(edition.ToString()))
        {
            using (p)
            {
                try
                {
                    var t = p.StartTime;
                    if (earliest == null || t < earliest) earliest = t;
                }
                catch
                {
                    // 權限不足或 process 已結束
                }
            }
        }
        return earliest;
    }

    private static string ReadLogTail(string path, int maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 解析 log 行首的時間，例如 "[2026-11-15 13:07:07.832607 +08:00] INFO ..."
    /// </summary>
    private static bool TryParseLogLineTimestamp(string text, int indexInLine, out DateTime localTime)
    {
        localTime = default;
        int lineStart = text.LastIndexOf('\n', Math.Max(0, indexInLine - 1)) + 1;
        if (lineStart >= text.Length || text[lineStart] != '[') return false;
        int close = text.IndexOf(']', lineStart);
        if (close < 0) return false;

        if (DateTimeOffset.TryParseExact(text[(lineStart + 1)..close], "yyyy-MM-dd HH:mm:ss.FFFFFFF zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
        {
            localTime = dto.LocalDateTime;
            return true;
        }
        return false;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }
        return count;
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
