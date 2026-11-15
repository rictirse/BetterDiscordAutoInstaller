using System.Diagnostics;
using System.Text.Json;
using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Platform;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Services;

/// <summary>
/// Discord 本體相關的共用流程；路徑、啟動方式、updater 狀態等平台差異都交給 IPlatformService
/// </summary>
public class DiscordService
{
    private static readonly HttpClient HttpClient = new();
    private readonly IPlatformService _platform;

    static DiscordService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public DiscordService(IPlatformService platform)
    {
        _platform = platform;
    }

    /// <summary>
    /// 打 Discord 官方 Manifest API 取得線上最新版本號。
    /// 只有由本工具主導 Discord 更新的平台（Windows）會用到，所以固定查 win / x64。
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
            // 取得本地目前 host 的版本（Windows：app-1.0.9180 -> 1.0.9180）
            var localVer = _platform.GetInstalledVersion(parentPath);

            // 取得線上最新版本
            string? remoteVersionStr = await GetLatestRemoteVersionAsync(edition);

            if (string.IsNullOrEmpty(remoteVersionStr))
            {
                Console.WriteLine($"[Warning] Failed to fetch remote version for {edition}. Skipping Discord version check.");
                return null;
            }

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

    /// <summary>目前使用中的 Discord host（Windows：app-x.x.x 資料夾；macOS：.app bundle）</summary>
    public string GetHostPath(string parentPath) => _platform.GetHostPath(parentPath);

    /// <summary>目前 host 的版本標記，記錄到設定檔用</summary>
    public string GetHostVersionLabel(string parentPath) => _platform.GetHostVersionLabel(parentPath);

    public bool IsDiscordRunning(DiscordEdition edition) => ProcessUtils.IsRunning(_platform.GetProcessName(edition));

    /// <summary>
    /// 結束所有 Discord process，並等到它們真的退出、檔案 handle 釋放後才回傳
    /// </summary>
    public async Task<bool> KillDiscordAsync(DiscordEdition edition, int timeoutMs = 10000)
    {
        string processName = _platform.GetProcessName(edition);

        // 平台支援時（macOS）先請 Discord 正常結束，幾秒內沒結束才強制結束
        if (!await TryGracefulExitAsync(edition, processName))
        {
            foreach (var proc in Process.GetProcessesByName(processName))
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
    /// 請 Discord 正常結束並等待最多 5 秒。平台不支援（Windows）時立即回傳 false，維持原本直接強制結束的行為
    /// </summary>
    private async Task<bool> TryGracefulExitAsync(DiscordEdition edition, string processName)
    {
        bool requested = false;
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            using (proc)
            {
                try
                {
                    if (_platform.RequestGracefulExit(proc))
                    {
                        Console.WriteLine($"Asking process to quit: {proc.ProcessName} (PID: {proc.Id})");
                        requested = true;
                    }
                }
                catch
                {
                    // process 可能已經自己結束
                }
            }
        }

        if (!requested) return false;

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (!IsDiscordRunning(edition)) return true;
            await Task.Delay(250);
        }

        return false;
    }

    /// <summary>
    /// 啟動 Discord。失敗時只回傳 false，不丟例外（避免整個流程中斷、Discord 就一直沒被開回來）
    /// </summary>
    public bool StartDiscord(DiscordEdition edition, string parentPath) => _platform.StartDiscord(edition, parentPath);

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
                reason = _platform.GetUpdaterBusyReason(edition, parentPath);
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
    /// 下載並安裝 Discord（Windows：下載官方安裝檔並等待安裝完成；macOS：提示使用者手動安裝）。
    /// 回傳 false 表示無法自動安裝
    /// </summary>
    public Task<bool> DownloadAndInstallDiscordAsync() => _platform.DownloadAndInstallDiscordAsync(HttpClient);
}
