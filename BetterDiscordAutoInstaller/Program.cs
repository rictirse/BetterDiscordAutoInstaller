using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Platform;
using BetterDiscordAutoInstaller.Services;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller;

internal class Program
{
    private static bool _isZh = false;

    private static async Task Main(string[] args)
    {
        // 判斷系統語系是否為中文 (包含 zh-TW, zh-CN, zh-HK 等)
        _isZh = Lang.IsZh;

        Console.WriteLine($"BetterDiscordAutoInstaller v{PathUtils.BdaiVersion}");

        if (!PlatformService.IsSupported)
        {
            Console.WriteLine(_isZh ? "目前只支援 Windows 與 macOS。" : "Only Windows and macOS are supported.");
            return;
        }

        var platform = PlatformService.Current;
        platform.PrepareWorkingDirectory();

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
            Console.WriteLine(platform.EnableAutostartMenuText);
            Console.WriteLine(platform.DisableAutostartMenuText);
            Console.WriteLine(_isZh ? "3. 自動更新" : "3. Run auto-update now");
            Console.WriteLine("==========================================");
            Console.Write(_isZh ? "請輸入選項 (1-3): " : "Enter your choice (1-3): ");

            string? input = Console.ReadLine()?.Trim();

            // 沒有可讀的輸入（stdin 已關閉）時直接結束，避免無限迴圈
            if (input == null) return;

            switch (input)
            {
                case "1":
                    platform.EnableAutostart();
                    return;
                case "2":
                    platform.DisableAutostart();
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

    // Discord 下載整個新版本可能要幾分鐘
    private static readonly TimeSpan UpdaterWaitTimeout = TimeSpan.FromMinutes(5);

    // 開機時 Windows 啟動 Discord 可能比本工具晚幾秒，先等一下再自己開
    private static readonly TimeSpan AutoStartGracePeriod = TimeSpan.FromSeconds(15);

    private sealed class UpdateContext
    {
        public required IPlatformService Platform { get; init; }
        public required ConfigService Config { get; init; }
        public required DiscordService Discord { get; init; }
        public required BetterDiscordService Bd { get; init; }
        public required PluginService Plugins { get; init; }
        public required PluginInfo Plugin { get; init; }
        public bool IsCi { get; init; }
        /// <summary>以命令列參數執行（例如開機捷徑），不是從互動選單選的</summary>
        public bool Unattended { get; init; }
        public bool BdNeedsDownload { get; set; }
        public bool PluginNeedsUpdate { get; set; }
        /// <summary>這次要處理的所有 edition。betterdiscord.asar 是共用的，取代前要一起關閉（見 StopOtherEditionsForAsarAsync）</summary>
        public required IReadOnlyDictionary<DiscordEdition, EditionState> Editions { get; init; }
    }

    private sealed class EditionState
    {
        public required string ParentPath { get; init; }

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
            await RunAutoUpdateCoreAsync(unattended: args.Length > 0);

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

    private static async Task RunAutoUpdateCoreAsync(bool unattended)
    {
        var configService = new ConfigService();
        configService.LoadSettings();

        var platform = PlatformService.Current;
        var discordService = new DiscordService(platform);
        var gitHubService = new GitHubService(configService, allowTokenPrompt: !unattended);
        var bdService = new BetterDiscordService(configService, gitHubService, platform);
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

        if (isCi && !platform.CanInjectBetterDiscord)
        {
            // 這些平台由 BD 官方 Installer 注入，它的 loader 固定載入 betterdiscord.asar
            Console.WriteLine($"[WARN] use_betterdiscord_ci_releases has no effect on {platform.Name}: the BetterDiscord loader installed by the official installer loads betterdiscord.asar, not betterdiscord-ci.asar.");
        }

        var ctx = new UpdateContext
        {
            Platform = platform,
            Config = configService,
            Discord = discordService,
            Bd = bdService,
            Plugins = pluginService,
            Plugin = defaultPlugin,
            IsCi = isCi,
            Unattended = unattended,
            BdNeedsDownload = await bdService.CheckForBdUpdatesAsync(isCi),
            PluginNeedsUpdate = await pluginService.CheckNeedsUpdateAsync(defaultPlugin),
            Editions = installedEditions.ToDictionary(x => x.Key, x => new EditionState { ParentPath = x.Value! })
        };

        foreach (var (edition, state) in ctx.Editions)
        {
            Console.WriteLine($"\nProcessing {edition} installation: {state.ParentPath}");

            try
            {
                await ProcessEditionAsync(ctx, edition, state.ParentPath, state);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to process {edition}: {ex.Message}");
            }
            finally
            {
                // 不論成功、失敗或中途例外，都要確保 Discord 回到該有的狀態
                await TryEnsureDiscordRunningAsync(ctx, edition, state);
            }
        }

        // 取代共用的 asar 時，可能關掉了已經處理完的 edition：最後再確認一次所有被關掉的都已重開
        foreach (var (edition, state) in ctx.Editions)
        {
            if (state.Relaunch) await TryEnsureDiscordRunningAsync(ctx, edition, state);
        }
    }

    private static async Task TryEnsureDiscordRunningAsync(UpdateContext ctx, DiscordEdition edition, EditionState state)
    {
        try
        {
            await EnsureDiscordRunningAsync(ctx, edition, state.ParentPath, state);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to start {edition}: {ex.Message}");
        }
    }

    /// <summary>
    /// 處理完一個 Discord 版本後，確保它在該執行的時候是開著的：
    ///   - 被本工具關掉（或為了更新而開啟）的 Discord → 一定重新開啟
    ///   - 自動模式下，Discord 有設定開機自動啟動，但更新完沒有自己開起來 → 順手開起來（目前只有 Windows 能判斷）
    /// 有開機自動啟動設定時沿用 Discord 登錄的指令，保留「啟動時最小化」等設定。
    /// </summary>
    private static async Task EnsureDiscordRunningAsync(UpdateContext ctx, DiscordEdition edition, string parentPath, EditionState state)
    {
        var discord = ctx.Discord;
        bool autoStart = UsesDiscordAutoStart(ctx, edition);

        if (!state.Relaunch && !autoStart) return;
        if (discord.IsDiscordRunning(edition)) return;

        if (state.Relaunch)
        {
            Console.WriteLine($"Restarting {edition}...");
        }
        else
        {
            // 本工具沒有動到 Discord，但它設定了開機自動啟動卻沒在執行：先給系統一點時間自己開
            Console.WriteLine($"{edition} is set to start with {ctx.Platform.Name} but is not running. Waiting up to {AutoStartGracePeriod.TotalSeconds:0}s...");
            if (await discord.WaitForDiscordStartAsync(edition, AutoStartGracePeriod))
            {
                Console.WriteLine($"{edition} started by itself.");
                return;
            }
            Console.WriteLine($"{edition} did not start by itself. Starting it now...");
        }

        StartDiscordForRun(ctx, edition, parentPath);
    }

    /// <summary>自動模式（開機捷徑 / 登入項目）且 Discord 有設定開機自動啟動</summary>
    private static bool UsesDiscordAutoStart(UpdateContext ctx, DiscordEdition edition)
        => ctx.Unattended && ctx.Discord.IsDiscordAutoStartEnabled(edition);

    /// <summary>自動模式下用 Discord 的開機指令啟動（保留「啟動時最小化」），其他情況用一般方式啟動</summary>
    private static bool StartDiscordForRun(UpdateContext ctx, DiscordEdition edition, string parentPath)
        => UsesDiscordAutoStart(ctx, edition)
            ? ctx.Discord.StartDiscordLikeAutoStart(edition, parentPath)
            : ctx.Discord.StartDiscord(edition, parentPath);

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

        // 1. Discord 本體是否需要更新（macOS 由 Discord 自己的 ShipIt 整包更新，本工具不介入）
        if (ctx.Platform.CanManageDiscordUpdates)
        {
            bool? isAppUpToDate = await discord.IsDiscordUpToDateAsync(edition, parentPath);
            if (isAppUpToDate == true)
            {
                Console.WriteLine($"{edition} app version is already up to date.");
            }
            else if (isAppUpToDate == false && !await UpdateDiscordAsync(ctx, edition, parentPath, state))
            {
                return;
            }
        }

        // 2. BetterDiscord 注入 / asar / plugin
        bool canInject = ctx.Platform.CanInjectBetterDiscord;
        string hostPath = discord.GetHostPath(parentPath);
        string hostVersion = discord.GetHostVersionLabel(parentPath);
        bool isInjected = canInject ? bd.IsBdInjected(hostPath, ctx.IsCi) : bd.HasBdLoader(hostPath);

        if (isInjected && !ctx.BdNeedsDownload && !ctx.PluginNeedsUpdate)
        {
            // 已經是最新版且已注入：不打擾使用者，也不關閉 Discord
            Console.WriteLine($"BetterDiscord ({edition}) is up to date and injected.");
            RememberPatchedVersion(ctx, edition, hostVersion);
            return;
        }

        if (!isInjected && !canInject)
        {
            // 本工具在這個平台不注入（原因見 MacPlatformService 說明），只能提示使用者
            Console.WriteLine(_isZh
                ? $"[提示] {edition} 目前沒有載入 BetterDiscord。{ctx.Platform.Name} 上本工具不會修改 Discord 本體，請先執行一次 BetterDiscord 官方 Installer（https://betterdiscord.app）；之後 Discord 每次更新，BetterDiscord 會自己重新注入。本工具會持續把 betterdiscord.asar 與 plugin 保持在最新版。"
                : $"[Info] BetterDiscord is not loaded in {edition}. On {ctx.Platform.Name} this tool does not modify the Discord app itself. Run the official BetterDiscord installer (https://betterdiscord.app) once; BetterDiscord then re-applies its injection after every Discord update. This tool keeps betterdiscord.asar and the plugin up to date.");
        }

        // 要動到檔案前先關閉 Discord（結束時會自動重開）。
        // 本工具不注入、BD 也還沒注入時，Discord 根本沒有載入 asar / plugin，不需要關閉
        if (canInject || isInjected)
        {
            await StopDiscordAsync(ctx, edition, state);
        }

        if (ctx.BdNeedsDownload)
        {
            await StopOtherEditionsForAsarAsync(ctx, edition);
            Console.WriteLine("Updating BetterDiscord asar...");
            if (await bd.UpdateBdAsarOnlyAsync(ctx.IsCi)) ctx.BdNeedsDownload = false;
        }

        if (!isInjected && canInject)
        {
            if (bd.IsAsarValid(ctx.IsCi))
            {
                Console.WriteLine("Applying BetterDiscord injection patch...");
                isInjected = bd.InjectPatch(hostPath, ctx.IsCi);
            }
            else
            {
                Console.WriteLine("[Error] Cannot apply BetterDiscord injection patch: BetterDiscord asar is missing or corrupted!");
            }
        }

        if (isInjected) RememberPatchedVersion(ctx, edition, hostVersion);

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

        string currentHost = discord.GetHostPath(parentPath);
        ctx.Bd.RevertInjectionForUpdate(currentHost);

        Console.WriteLine($"Starting {edition} to apply the update...");
        if (!StartDiscordForRun(ctx, edition, parentPath)) return true;
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

    /// <summary>
    /// betterdiscord.asar 是所有 edition 共用的。Windows 上開著的 asar 會被鎖住，只關閉目前這個 edition 時，
    /// 另一個 edition 還開著就會取代失敗。所以取代前先關閉其他正在執行的 edition（結束時會重開）。
    /// 條件和步驟 2 一樣：本工具會注入的平台一律關閉，否則只關閉有載入 BD 的。
    /// updater 正在忙的 edition 不關閉，避免更新到一半被砍掉；這次取代可能失敗（原子寫入會保留舊檔），下次執行再試。
    /// </summary>
    private static async Task StopOtherEditionsForAsarAsync(UpdateContext ctx, DiscordEdition current)
    {
        foreach (var (edition, state) in ctx.Editions)
        {
            if (edition == current || !ctx.Discord.IsDiscordRunning(edition)) continue;

            string? busyReason = ctx.Platform.GetUpdaterBusyReason(edition, state.ParentPath);
            if (busyReason != null)
            {
                Console.WriteLine($"[WARN] {edition} is updating ({busyReason}). Leaving it open; the shared BetterDiscord asar may not be replaced this time.");
                continue;
            }

            if (!ctx.Platform.CanInjectBetterDiscord && !HasBdLoader(ctx, state.ParentPath)) continue;

            Console.WriteLine($"{edition} also uses the shared BetterDiscord asar.");
            await StopDiscordAsync(ctx, edition, state);
        }
    }

    private static bool HasBdLoader(UpdateContext ctx, string parentPath)
    {
        try
        {
            return ctx.Bd.HasBdLoader(ctx.Discord.GetHostPath(parentPath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Could not check BetterDiscord in {parentPath}: {ex.Message}");
            return false;
        }
    }

    private static void RememberPatchedVersion(UpdateContext ctx, DiscordEdition edition, string hostVersion)
    {
        if (ctx.Config.GetLastInstalledVersion(edition) == hostVersion) return;

        ctx.Config.SetDiscordVersion(edition, hostVersion);
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

        // macOS 無法自動安裝（只會提示使用者手動安裝），直接結束
        if (!await discordService.DownloadAndInstallDiscordAsync()) return null;

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