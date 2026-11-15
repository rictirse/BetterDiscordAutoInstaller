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

    // Discord 下載整個新版本可能要幾分鐘
    private static readonly TimeSpan UpdaterWaitTimeout = TimeSpan.FromMinutes(5);

    private sealed class UpdateContext
    {
        public required ConfigService Config { get; init; }
        public required DiscordService Discord { get; init; }
        public required BetterDiscordService Bd { get; init; }
        public required PluginService Plugins { get; init; }
        public required PluginInfo Plugin { get; init; }
        public bool IsCi { get; init; }
        public bool BdNeedsDownload { get; set; }
        public bool PluginNeedsUpdate { get; set; }
    }

    private sealed class EditionState
    {
        /// <summary>這次執行中由本工具關掉（或為了更新而開啟）Discord，結束時要確保它在執行</summary>
        public bool Relaunch { get; set; }
    }

    /// <summary>
    /// BetterDiscord 自動更新邏輯
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    private static async Task RunAutoUpdateAsync(string[] args)
    {
        try
        {
            await RunAutoUpdateCoreAsync();

            Console.WriteLine(_isZh
                ? "\n安裝完成。3 秒後自動退出..."
                : "\nInstallation complete. Exiting in 3 seconds...");
        }
        catch (Exception ex)
        {
            Console.WriteLine(_isZh
                ? $"\n[錯誤] 自動更新時發生未預期的錯誤: {ex.Message}\n3 秒後自動退出..."
                : $"\n[Error] Unexpected error during auto-update: {ex.Message}\nExiting in 3 seconds...");
        }

        await Task.Delay(3000);
    }

    private static async Task RunAutoUpdateCoreAsync()
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
        var installedEditions = await EnsureDiscordInstalledAsync(editions, configService, discordService);
        if (installedEditions == null) return;

        bool isCi = configService.Config.UseBetterDiscordCiReleases;
        var defaultPlugin = new PluginInfo("https://raw.githubusercontent.com/riolubruh/YABDP4Nitro/main/YABDP4Nitro.plugin.js");

        var ctx = new UpdateContext
        {
            Config = configService,
            Discord = discordService,
            Bd = bdService,
            Plugins = pluginService,
            Plugin = defaultPlugin,
            IsCi = isCi,
            BdNeedsDownload = await bdService.CheckForBdUpdatesAsync(isCi),
            PluginNeedsUpdate = await pluginService.CheckNeedsUpdateAsync(defaultPlugin)
        };

        foreach (var (edition, parentPath) in installedEditions)
        {
            Console.WriteLine($"\nProcessing {edition} installation: {parentPath!}");
            var state = new EditionState();

            try
            {
                await ProcessEditionAsync(ctx, edition, parentPath!, state);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to process {edition}: {ex.Message}");
            }
            finally
            {
                // 不論成功、失敗或中途例外，只要 Discord 是被本工具關掉的，就一定要開回來
                if (state.Relaunch && !discordService.IsDiscordRunning(edition))
                {
                    Console.WriteLine($"Restarting {edition}...");
                    discordService.StartDiscord(edition, parentPath!);
                }
            }
        }
    }

    private static async Task ProcessEditionAsync(UpdateContext ctx, DiscordEdition edition, string parentPath, EditionState state)
    {
        var discord = ctx.Discord;
        var bd = ctx.Bd;

        // 0. Discord 已在執行（例如開機時被 Windows 自動啟動）：先等它內建的 updater 跑完，避免更新到一半被砍掉
        if (discord.IsDiscordRunning(edition) &&
            !await discord.WaitForUpdaterIdleAsync(edition, parentPath, UpdaterWaitTimeout))
        {
            Console.WriteLine($"[WARN] {edition} is still updating. Leaving it alone this time to avoid breaking the update.");
            return;
        }

        // 1. Discord 本體是否需要更新
        bool? isAppUpToDate = await discord.IsDiscordUpToDateAsync(edition, parentPath);
        if (isAppUpToDate == true)
        {
            Console.WriteLine($"{edition} app version is already up to date.");
        }
        else if (isAppUpToDate == false && !await UpdateDiscordAsync(ctx, edition, parentPath, state))
        {
            return;
        }

        // 2. BetterDiscord 注入 / asar / plugin
        string coreFolder = discord.GetLatestInstalledDiscordFolder(parentPath);
        string fullDiscordPath = Path.Combine(parentPath, coreFolder);
        bool isInjected = bd.IsBdInjected(fullDiscordPath, ctx.IsCi);

        if (isInjected && !ctx.BdNeedsDownload && !ctx.PluginNeedsUpdate)
        {
            // 已經是最新版且已注入：不打擾使用者，也不關閉 Discord
            Console.WriteLine($"BetterDiscord ({edition}) is up to date and injected.");
            RememberPatchedVersion(ctx, edition, coreFolder);
            return;
        }

        // 要動到檔案前先關閉 Discord（結束時會自動重開）
        await StopDiscordAsync(ctx, edition, state);

        if (ctx.BdNeedsDownload)
        {
            Console.WriteLine("Updating BetterDiscord asar...");
            if (await bd.UpdateBdAsarOnlyAsync(ctx.IsCi)) ctx.BdNeedsDownload = false;
        }

        if (!isInjected)
        {
            if (bd.IsAsarValid(ctx.IsCi))
            {
                Console.WriteLine("Applying BetterDiscord injection patch...");
                isInjected = bd.InjectPatch(fullDiscordPath, ctx.IsCi);
            }
            else
            {
                Console.WriteLine("[Error] Cannot apply BetterDiscord injection patch: BetterDiscord asar is missing or corrupted!");
            }
        }

        if (isInjected) RememberPatchedVersion(ctx, edition, coreFolder);

        if (ctx.PluginNeedsUpdate)
        {
            Console.WriteLine($"[INFO] Plugin update detected for {ctx.Plugin.Name}.");
            await ctx.Plugins.DownloadPluginAsync(ctx.Plugin);
            ctx.PluginNeedsUpdate = false;
        }
    }

    /// <summary>
    /// Discord 有新版本時，由本工具主導更新：
    /// 先把 BetterDiscord 的注入還原成原廠檔案，否則 Discord 的 delta 更新會因 hash 驗證失敗
    /// （PatchFailed / NotFound），然後才啟動 Discord 讓它更新。
    /// 回傳 false 表示 Discord 仍在更新中，這次先不要動它。
    /// </summary>
    private static async Task<bool> UpdateDiscordAsync(UpdateContext ctx, DiscordEdition edition, string parentPath, EditionState state)
    {
        var discord = ctx.Discord;
        Console.WriteLine($"{edition} is outdated. Preparing Discord's updater...");

        await StopDiscordAsync(ctx, edition, state);

        string currentHost = Path.Combine(parentPath, discord.GetLatestInstalledDiscordFolder(parentPath));
        ctx.Bd.RevertInjectionForUpdate(currentHost);

        Console.WriteLine($"Starting {edition} to apply the update...");
        if (!discord.StartDiscord(edition, parentPath)) return true;
        state.Relaunch = true;

        if (!await discord.WaitForDiscordStartAsync(edition, TimeSpan.FromSeconds(30)))
        {
            Console.WriteLine($"[WARN] {edition} did not start within 30s.");
        }

        if (!await discord.WaitForUpdaterIdleAsync(edition, parentPath, UpdaterWaitTimeout))
        {
            Console.WriteLine($"[WARN] {edition} is still updating. BetterDiscord will be injected on the next run.");
            return false;
        }

        if (await discord.IsDiscordUpToDateAsync(edition, parentPath) == false)
        {
            Console.WriteLine($"[WARN] {edition} update did not complete. Discord will retry by itself; re-applying BetterDiscord for now.");
        }

        return true;
    }

    private static async Task StopDiscordAsync(UpdateContext ctx, DiscordEdition edition, EditionState state)
    {
        if (!ctx.Discord.IsDiscordRunning(edition)) return;

        Console.WriteLine($"\nClosing {edition}...");
        state.Relaunch = true;
        await ctx.Discord.KillDiscordAsync(edition);
    }

    private static void RememberPatchedVersion(UpdateContext ctx, DiscordEdition edition, string coreFolder)
    {
        if (ctx.Config.GetLastInstalledVersion(edition) == coreFolder) return;

        ctx.Config.SetDiscordVersion(edition, coreFolder);
        ctx.Config.SaveSettings();
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