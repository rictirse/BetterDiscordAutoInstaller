using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Services;
using BetterDiscordAutoInstaller.Utils;
using System.Diagnostics;
using System.Globalization;

namespace BetterDiscordAutoInstaller;

internal class Program
{
    private static readonly string ShortcutName = "BetterDiscordAutoInstaller.lnk";
    private static bool _isZh = false;

    private static async Task Main(string[] args)
    {
        // 判斷系統語系是否為中文 (包含 zh-TW, zh-CN, zh-HK 等)
        _isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"BetterDiscordAutoInstaller v{PathUtils.BdaiVersion}");

        // 如果啟動參數包含 "3" 或 "-3" 或 "--auto"，直接執行自動更新流程
        if (args.Length > 0 && (args[0] == "3" || args[0] == "-3" || args[0] == "--auto"))
        {
            await RunAutoUpdateAsync(args);
            return;
        }

        // 無參數時顯示選單
        while (true)
        {
            Console.WriteLine("\n==========================================");
            Console.WriteLine(_isZh ? "請選擇要執行的功能：" : "Please select an option:");
            Console.WriteLine(_isZh ? "1. 每次開機的時候自動啟動自動更新" : "1. Enable auto-update on Windows startup");
            Console.WriteLine(_isZh ? "2. 解除每次開機的時候自動更新" : "2. Disable auto-update on Windows startup");
            Console.WriteLine(_isZh ? "3. 自動更新" : "3. Run auto-update now");
            Console.WriteLine("==========================================");
            Console.Write(_isZh ? "請輸入選項 (1-3): " : "Enter your choice (1-3): ");

            string? input = Console.ReadLine()?.Trim();

            switch (input)
            {
                case "1":
                    EnableStartup();
                    return;
                case "2":
                    DisableStartup();
                    return;
                case "3":
                    await RunAutoUpdateAsync(args);
                    return;
                default:
                    Console.WriteLine(_isZh ? "無效的選項，請重新輸入！" : "Invalid option, please try again!");
                    break;
            }
        }
    }

    /// <summary>
    /// 在 Startup 資料夾建立帶有參數 3 的捷徑
    /// </summary>
    private static void EnableStartup()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);

            string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath))
            {
                Console.WriteLine(_isZh ? "無法取得當前程式路徑，設定失敗。" : "Failed to retrieve current executable path.");
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

            Console.WriteLine(_isZh
                ? "\n[成功] 已將捷徑加入開機啟動資料夾 (帶入參數 3)！"
                : "\n[Success] Shortcut added to Windows Startup folder (with argument 3)!");
        }
        catch (Exception ex)
        {
            Console.WriteLine(_isZh
                ? $"\n[失敗] 設定開機啟動時發生錯誤: {ex.Message}"
                : $"\n[Failed] Error enabling startup: {ex.Message}");
        }
    }

    /// <summary>
    /// 移除 Startup 資料夾內的捷徑
    /// </summary>
    private static void DisableStartup()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string shortcutPath = Path.Combine(startupFolder, ShortcutName);

            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
                Console.WriteLine(_isZh
                    ? "\n[成功] 已移除開機自動啟動捷徑！"
                    : "\n[Success] Startup shortcut removed successfully!");
            }
            else
            {
                Console.WriteLine(_isZh
                    ? "\n[提示] 尚未設定開機自動啟動捷徑，無需移除。"
                    : "\n[Info] No startup shortcut was found.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(_isZh
                ? $"\n[失敗] 移除捷徑時發生錯誤: {ex.Message}"
                : $"\n[Failed] Error removing startup shortcut: {ex.Message}");
        }
    }

    /// <summary>
    /// BetterDiscord 自動更新邏輯
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private static async Task RunAutoUpdateAsync(string[] args)
    {
        var configService = new ConfigService();
        configService.LoadSettings();

        var discordService = new DiscordService();
        var gitHubService = new GitHubService(configService);
        var bdService = new BetterDiscordService(configService, gitHubService);
        var pluginService = new PluginService();

        var editions = new Dictionary<DiscordEdition, string?>
        {
            { DiscordEdition.Discord, configService.Config.DiscordInstalledPath ?? PathUtils.GetDefaultDiscordPath(DiscordEdition.Discord) },
            { DiscordEdition.DiscordCanary, configService.Config.DiscordCanaryInstalledPath },
            { DiscordEdition.DiscordPTB, configService.Config.DiscordPtbInstalledPath }
        }
        .Where(x => !string.IsNullOrEmpty(x.Value)
                 && Directory.Exists(x.Value))
        .ToDictionary();

        // 檢查並確保 Discord 已安裝 (若失敗則直接退出)
        var _editions = await EnsureDiscordInstalledAsync(editions, configService, discordService);
        if (editions == null)
        {
            await Task.Delay(3000);
            return;
        }
        else
        {
            editions = _editions!;
        }

        bool isCi = configService.Config.UseBetterDiscordCiReleases;
        bool bdHasUpdates = await bdService.CheckForBdUpdatesAsync(isCi);

        foreach (var (edition, parentPath) in editions)
        {
            Console.WriteLine($"\nProcessing {edition} installation: {parentPath!}");
            bool ifDiscordRunning = discordService.IsDiscordRunning(edition);

            // 1. 先用 API 檢查本地版本是否已是官方最新版本
            bool isAppUpToDate = await discordService.IsDiscordUpToDateAsync(edition, parentPath!);

            // 2. 如果不是最新版，且 Discord 目前沒在執行，才喚起 Updater 更新
            if (!isAppUpToDate && !ifDiscordRunning)
            {
                Console.WriteLine($"{edition} is outdated. Starting updater...");
                discordService.StartDiscord(edition, parentPath!);
                await Task.Delay(1000);

                while (discordService.IsDiscordUpdating(edition))
                {
                    await Task.Delay(500);
                }
            }
            else if (isAppUpToDate)
            {
                Console.WriteLine($"{edition} app version is already up to date.");
            }

            string coreFolder = discordService.GetLatestInstalledDiscordFolder(parentPath!);
            string fullDiscordPath = Path.Combine(parentPath!, coreFolder);

            bool isLastPatchUpToDate = coreFolder == configService.GetLastInstalledVersion(edition);
            bool isInjected = bdService.IsBdInjected(fullDiscordPath, isCi);

            if (!isLastPatchUpToDate || !isInjected || bdHasUpdates)
            {
                Console.WriteLine($"\nKilling any running {edition} processes...");
                discordService.KillDiscord(edition);
                await Task.Delay(1500);
                ifDiscordRunning = false;

                if (!isLastPatchUpToDate)
                {
                    configService.SetDiscordVersion(edition, coreFolder);
                    configService.SaveSettings();
                    isInjected = false;
                }

                bool bdAsarExists = File.Exists(PathUtils.BdAsarPath);

                if (bdHasUpdates || !bdAsarExists)
                {
                    Console.WriteLine(bdAsarExists
                        ? "Updating BetterDiscord asar..."
                        : "BetterDiscord core file missing. Downloading betterdiscord.asar...");

                    await bdService.UpdateBdAsarOnlyAsync(isCi);
                    bdHasUpdates = false;

                    bdAsarExists = File.Exists(PathUtils.BdAsarPath);
                }

                if (!isInjected)
                {
                    if (bdAsarExists)
                    {
                        Console.WriteLine($"Applying BetterDiscord injection patch...");
                        bdService.InjectPatch(fullDiscordPath, isCi);
                    }
                    else
                    {
                        Console.WriteLine($"[Error] Cannot apply BetterDiscord injection patch: betterdiscord.asar is missing!");
                    }
                }
            }
            else
            {
                Console.WriteLine($"BetterDiscord ({edition}) is up to date and injected.");
            }

            // 1. 將 Plugin 初始化與版本檢查抽到最前面
            var defaultPlugin = new PluginInfo("https://raw.githubusercontent.com/riolubruh/YABDP4Nitro/main/YABDP4Nitro.plugin.js");

            // 檢查本地是否缺少套件或是需要更新
            bool needsUpdate = await pluginService.CheckNeedsUpdateAsync(defaultPlugin);

            if (ifDiscordRunning)
            {
                // ==========================================
                // 情境 A：Discord 目前正在執行中
                // ==========================================
                if (needsUpdate)
                {
                    Console.WriteLine($"[INFO] Plugin update detected for {defaultPlugin.Name}. Closing Discord to apply update...");

                    // 1. 先關閉 Discord，避免檔案鎖定或無效寫入
                    discordService.KillDiscord(edition);

                    // 2. 下載 / 更新套件
                    await pluginService.DownloadPluginAsync(defaultPlugin);

                    // 3. 更新完後重啟 Discord
                    if (edition.ToString() == configService.Config.RerunDiscordEdition)
                    {
                        Console.WriteLine($"Restarting {edition}...");
                        discordService.StartDiscord(edition, parentPath!);
                    }
                }
                else
                {
                    // 已經是最新版，且 Discord 本來就在運作，直接跳過、不打擾使用者
                    Console.WriteLine($"BetterDiscord ({edition}) is up to date. No restart needed.");
                }
            }
            else
            {
                // === 情境 B：Discord 目前還開著的流程 ===
                if (needsUpdate)
                {
                    Console.WriteLine($"[INFO] Plugin update detected. Closing {edition} to apply update...");

                    // 1. 先關閉 Discord
                    discordService.KillDiscord(edition);

                    // 2. 下載 / 更新套件
                    await pluginService.DownloadPluginAsync(defaultPlugin);

                    // 3. 更新完成後重新啟動 Discord
                    Console.WriteLine($"Restarting {edition}...");
                    discordService.StartDiscord(edition, parentPath!);
                }
                else
                {
                    Console.WriteLine($"BetterDiscord ({edition}) is up to date and injected.");
                }
            }
        }

        Console.WriteLine(_isZh
            ? "\n安裝完成。3 秒後自動退出..."
            : "\nInstallation complete. Exiting in 3 seconds...");

    }

    /// <summary>
    /// 檢查並確保 Discord 已安裝，若未安裝則自動下載並啟動安裝程式。
    /// 若 Discord 已就緒（或安裝成功）回傳 true，失敗回傳 false
    /// </summary>
    /// <param name="editions"></param>
    /// <param name="configService"></param>
    /// <param name="discordService"></param>
    /// <returns></returns>
    private static async Task<Dictionary<DiscordEdition, string?>?> EnsureDiscordInstalledAsync(
        Dictionary<DiscordEdition, string?> editions,
        ConfigService configService,
        DiscordService discordService)
    {
        if (editions.Count > 0) return editions;

        await discordService.DownloadAndInstallDiscordAsync();

        Console.WriteLine(_isZh
            ? "\n[提示] 正在等待 Discord 完成初始化並建立安裝資料夾..."
            : "\n[Info] Waiting for Discord to complete initialization and create installation folder...");

        bool isInstalledSuccess = false;
        int maxWaitSeconds = 10;
        var path = configService.Config.DiscordInstalledPath ?? PathUtils.GetDefaultDiscordPath(DiscordEdition.Discord);
        if (string.IsNullOrWhiteSpace(path)) return null;

        for (int i = 1; i <= maxWaitSeconds; i++)
        {
            if (Directory.Exists(path))
            {
                isInstalledSuccess = true;
                Console.WriteLine(_isZh
                    ? $"\r[成功] 已偵測到 Discord 安裝目錄！（耗時 {i} 秒）".PadRight(50)
                    : $"\r[Success] Discord installation path detected! ({i}s elapsed)".PadRight(50));
                break;
            }

            string dots = new('.', (i - 1) % 3 + 1);
            Console.Write(_isZh
                ? $"\r檢查中 ({i}/{maxWaitSeconds} 秒){dots.PadRight(3)} 正在等待 Discord 安裝檔案產生..."
                : $"\rChecking ({i}/{maxWaitSeconds}s){dots.PadRight(3)} Waiting for Discord files to appear...");

            await Task.Delay(1000);
        }

        Console.WriteLine();

        if (!isInstalledSuccess)
        {
            Console.WriteLine(_isZh
                ? "\n[錯誤] 等待逾時（10秒），未偵測到 Discord 安裝目錄，自動更新流程取消。"
                : "\n[Error] Timeout (10s): Discord installation path not found. Aborting auto-update.");
            return null;
        }

        return new Dictionary<DiscordEdition, string?>
        {
            { DiscordEdition.Discord, path},
        };
    }
}