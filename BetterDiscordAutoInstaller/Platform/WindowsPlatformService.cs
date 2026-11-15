using System.Diagnostics;
using System.Runtime.Versioning;
using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Platform;

/// <summary>
/// Windows：
///   - Discord 安裝在 %LocalAppData%\Discord\app-x.x.x（新版 updater），模組在 app-x.x.x\modules\discord_desktop_core-N
///   - BetterDiscord 資料在 %AppData%\BetterDiscord
///   - updater log 在 %AppData%\discord\logs\Discord_updater_rCURRENT.log
///   - 開機自動執行用 Startup 資料夾的捷徑
/// 這裡的程式碼是從原本的 DiscordService / BetterDiscordService / Program 原封不動搬過來的，行為不變。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformService : IPlatformService
{
    private const string ShortcutName = "BetterDiscordAutoInstaller.lnk";
    private const string OfficialDiscordDownloadUrl = "https://discord.com/api/downloads/distributions/app/installers/latest?channel=stable&platform=win&arch=x64";

    // updater 在兩個動作之間最長會停約 3 秒（例如 CollectGarbage → InstallModule），留一點餘裕
    private static readonly TimeSpan LogQuietPeriod = TimeSpan.FromSeconds(8);

    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string Name => "Windows";
    public bool CanManageDiscordUpdates => true;
    public bool CanInjectBetterDiscord => true;

    public string BetterDiscordFolder => Path.Combine(AppData, "BetterDiscord");

    public string GetDefaultDiscordPath(DiscordEdition edition) => Path.Combine(LocalAppData, edition.ToString());

    public string GetHostPath(string installPath) => Path.Combine(installPath, GetLatestInstalledDiscordFolder(installPath));

    public string GetHostVersionLabel(string installPath) => GetLatestInstalledDiscordFolder(installPath);

    // 取得本地最新 app-x.x.x 資料夾並去頭（如 app-1.0.9180 -> 1.0.9180）
    public Version? GetInstalledVersion(string installPath) => ParseAppFolderVersion(GetLatestInstalledDiscordFolder(installPath));

    public string GetResourcesPath(string hostPath) => Path.Combine(hostPath, "resources");

    /// <summary>
    /// 取得 discord_desktop_core 模組資料夾；有多個版本時取編號最大的（Discord 載入的那一個）
    /// </summary>
    public string? FindDesktopCoreDir(string hostPath)
    {
        var modulesPath = Path.Combine(hostPath, "modules");
        if (!Directory.Exists(modulesPath)) return null;

        const string prefix = "discord_desktop_core-";
        var latest = Directory.GetDirectories(modulesPath, prefix + "*")
            .Select(dir => (Dir: dir, Num: int.TryParse(Path.GetFileName(dir)[prefix.Length..], out var n) ? n : -1))
            .OrderBy(x => x.Num)
            .LastOrDefault();

        return latest.Dir == null ? null : Path.Combine(latest.Dir, "discord_desktop_core");
    }

    private static string GetLatestInstalledDiscordFolder(string parentPath)
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

    public string GetProcessName(DiscordEdition edition) => edition.ToString();

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

    // Windows 維持原本的行為：直接強制結束整個 process tree
    public bool RequestGracefulExit(Process process) => false;

    private static string GetLogFilePath(DiscordEdition edition)
    {
        return Path.Combine(AppData, edition.ToString().ToLower(), "logs", $"{edition}_updater_rCURRENT.log");
    }

    /// <summary>
    /// 回傳 updater 忙碌的原因；閒置時回傳 null
    /// </summary>
    public string? GetUpdaterBusyReason(DiscordEdition edition, string parentPath)
    {
        string logPath = GetLogFilePath(edition);
        bool logExists = File.Exists(logPath);

        // log 還在持續寫入 → updater 正在工作（也涵蓋 Discord 更新完自我重啟的空檔）
        if (logExists && DateTime.Now - File.GetLastWriteTime(logPath) < LogQuietPeriod)
            return "updater is active";

        string processName = GetProcessName(edition);
        if (!ProcessUtils.IsRunning(processName)) return null;

        // 新版 updater 會先把 host 下載到 ".app-x.x.x.installing"，完成後才改名成 "app-x.x.x"
        if (Directory.Exists(parentPath) && Directory.EnumerateDirectories(parentPath, ".app-*.installing").Any())
            return "downloading a new Discord version";

        DateTime? processStart = ProcessUtils.GetEarliestStartTime(processName);

        if (!logExists)
        {
            // 沒有 log 可參考：至少讓 Discord 啟動一段時間再動它
            return processStart != null && DateTime.Now - processStart.Value < TimeSpan.FromSeconds(20) ? "starting up" : null;
        }

        return UpdaterLog.Analyze(UpdaterLog.ReadTail(logPath, 512 * 1024), processStart, DateTime.Now);
    }

    /// <summary>
    /// 下載 Discord 官方安裝檔至 %temp% 並自動啟動安裝，且等待安裝完成
    /// </summary>
    public async Task<bool> DownloadAndInstallDiscordAsync(HttpClient http)
    {
        string tempPath = Path.GetTempPath();
        string installerPath = Path.Combine(tempPath, "DiscordSetup.exe");

        Console.WriteLine($"Downloading Discord installer to: {installerPath}");

        using (var response = await http.GetAsync(OfficialDiscordDownloadUrl, HttpCompletionOption.ResponseHeadersRead))
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

        return true;
    }

    public string EnableAutostartMenuText => Lang.T("1. 每次開機的時候自動啟動自動更新", "1. Enable auto-update on Windows startup");
    public string DisableAutostartMenuText => Lang.T("2. 解除每次開機的時候自動更新", "2. Disable auto-update on Windows startup");

    /// <summary>
    /// 在 Startup 資料夾建立帶有參數 3 的捷徑
    /// </summary>
    public void EnableAutostart()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);

            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                Console.WriteLine(Lang.T("無法取得當前程式路徑，設定失敗。", "Failed to retrieve current executable path."));
                return;
            }

            string targetDirectory = Path.GetDirectoryName(exePath) ?? "";

            string powershellCommand = "$s = (New-Object -COM WScript.Shell).CreateShortcut('" + shortcutPath + "'); " +
                                       "$s.TargetPath = '" + exePath + "'; " +
                                       "$s.Arguments = '3'; " +
                                       "$s.WorkingDirectory = '" + targetDirectory + "'; " +
                                       "$s.Save()";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -Command \"{powershellCommand}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            process?.WaitForExit();

            Console.WriteLine(Lang.T(
                "\n[成功] 已將捷徑加入開機啟動資料夾 (帶入參數 3)！",
                "\n[Success] Shortcut added to Windows Startup folder (with argument 3)!"));
        }
        catch (Exception ex)
        {
            Console.WriteLine(Lang.T(
                $"\n[失敗] 設定開機啟動時發生錯誤: {ex.Message}",
                $"\n[Failed] Error enabling startup: {ex.Message}"));
        }
    }

    /// <summary>
    /// 移除 Startup 資料夾內的捷徑
    /// </summary>
    public void DisableAutostart()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);

            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
                Console.WriteLine(Lang.T(
                    "\n[成功] 已移除開機自動啟動捷徑！",
                    "\n[Success] Startup shortcut removed successfully!"));
            }
            else
            {
                Console.WriteLine(Lang.T(
                    "\n[提示] 尚未設定開機自動啟動捷徑，無需移除。",
                    "\n[Info] No startup shortcut was found."));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(Lang.T(
                $"\n[失敗] 移除捷徑時發生錯誤: {ex.Message}",
                $"\n[Failed] Error removing startup shortcut: {ex.Message}"));
        }
    }

    // Windows：雙擊或捷徑（WorkingDirectory 設為程式所在資料夾）啟動，維持原本行為
    public void PrepareWorkingDirectory()
    {
    }
}
