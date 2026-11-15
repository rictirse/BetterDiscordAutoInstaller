using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Platform;
using BetterDiscordAutoInstaller.Utils;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BetterDiscordAutoInstaller.Services;

public class BetterDiscordService
{
    private const string BdReleaseDownloadBase = "https://github.com/BetterDiscord/BetterDiscord/releases/latest/download/";
    private const string BdLatestReleaseUrl = "https://github.com/BetterDiscord/BetterDiscord/releases/latest";

    // Discord 原廠 discord_desktop_core\index.js 的完整內容（CRLF 結尾）。
    // Discord 的 delta 更新會驗證這個檔案的 SHA256，內容必須一個 byte 都不差。
    private const string StockIndexJsContent = "module.exports = require('./core.asar');\r\n";

    // 本工具（以及舊版 BD 安裝程式）加在 index.js 最前面的那一行
    private static readonly Regex BdRequireLineRegex = new(
        @"^[ \t]*require\(""[^""\r\n]*betterdiscord[^""\r\n]*\.asar""\);[ \t]*(\r\n|\n|$)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly ConfigService _configService;
    private readonly GitHubService _gitHubService;
    private readonly IPlatformService _platform;
    private Dictionary<string, string>? _remoteChecksums;

    public BetterDiscordService(ConfigService configService, GitHubService gitHubService, IPlatformService platform)
    {
        _configService = configService;
        _gitHubService = gitHubService;
        _platform = platform;
    }

    public string GetAsarPath(bool isCi) => isCi ? PathUtils.BdCiAsarPath : PathUtils.BdAsarPath;

    private string GetRequireLine(bool isCi) => $"require(\"{GetAsarPath(isCi).Replace("\\", "/")}\");";

    /// <summary>
    /// asar 存在且結構、內容 hash 都完整
    /// </summary>
    public bool IsAsarValid(bool isCi) => AsarArchive.TryComputeFileHashes(GetAsarPath(isCi)) != null;

    /// <summary>
    /// 取得 discord_desktop_core 模組資料夾（位置依平台而定，見 IPlatformService.FindDesktopCoreDir）
    /// </summary>
    private string? FindDesktopCoreDir(string discordPath) => _platform.FindDesktopCoreDir(discordPath);

    /// <summary>
    /// BetterDiscord 是否會在這個 Discord host 啟動時被載入。
    /// 本工具的 discord_desktop_core 注入，或 BetterDiscord 1.14+ 自己的 resources\app 注入，任一個存在即可。
    /// </summary>
    public bool IsBdInjected(string discordPath, bool isCi) => IsCoreInjected(discordPath, isCi) || IsResourcesInjected(discordPath, isCi);

    private bool IsCoreInjected(string discordPath, bool isCi)
    {
        var coreDir = FindDesktopCoreDir(discordPath);
        if (coreDir == null) return false;

        var indexJs = Path.Combine(coreDir, "index.js");
        if (!File.Exists(indexJs)) return false;

        return File.ReadAllText(indexJs).Contains(GetRequireLine(isCi), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// BetterDiscord 1.14+ 在 Discord 結束時會自己做 "migration"：
    /// 把新版 host 的 resources\app.asar 改名為 betterdiscord.app.asar，並建立 resources\app\index.js 先載入 BD。
    /// </summary>
    private bool IsResourcesInjected(string discordPath, bool isCi)
    {
        var resources = _platform.GetResourcesPath(discordPath);
        var appIndexJs = Path.Combine(resources, "app", "index.js");
        if (!File.Exists(appIndexJs) || !File.Exists(Path.Combine(resources, "betterdiscord.app.asar"))) return false;

        // BD 用 JSON.stringify 寫路徑，反斜線會是跳脫後的 "\\"
        var content = File.ReadAllText(appIndexJs).Replace("\\\\", "/").Replace('\\', '/');
        var asarPath = GetAsarPath(isCi).Replace('\\', '/');
        return content.Contains(asarPath + "/", StringComparison.OrdinalIgnoreCase)
            || content.Contains(asarPath + "\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// resources\app\index.js 是否有任何 BetterDiscord loader（不限定 asar 路徑）。
    /// 用在本工具不負責注入的平台（macOS）：BD 官方 Installer 的 index.js 是在執行時才組出 asar 路徑，
    /// 無法用 IsResourcesInjected 的路徑比對判斷，這裡只確認 BD 的 loader 還在。
    /// </summary>
    public bool HasBdLoader(string discordPath)
    {
        try
        {
            var resources = _platform.GetResourcesPath(discordPath);
            var appIndexJs = Path.Combine(resources, "app", "index.js");
            return File.Exists(appIndexJs)
                && File.Exists(Path.Combine(resources, "betterdiscord.app.asar"))
                && File.ReadAllText(appIndexJs).Contains("betterdiscord", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在 discord_desktop_core\index.js 最前面加上 require 行。
    /// 原本的內容保持 byte 不變，之後才能精準還原成原廠檔案（讓 Discord 的 delta 更新通過 hash 驗證）。
    /// </summary>
    public bool InjectPatch(string discordPath, bool isCi)
    {
        var coreDir = FindDesktopCoreDir(discordPath);
        if (coreDir == null || !File.Exists(Path.Combine(coreDir, "core.asar")))
        {
            // 模組還沒裝完（Discord 可能正在更新）。不要自己生一個 index.js，否則 Discord 會以為模組已安裝而壞掉
            Console.WriteLine($"[ERROR] discord_desktop_core is missing or incomplete in {discordPath}. Skipping injection.");
            return false;
        }

        var indexJs = Path.Combine(coreDir, "index.js");
        string current = File.Exists(indexJs) ? File.ReadAllText(indexJs) : StockIndexJsContent;
        string patched = GetRequireLine(isCi) + "\r\n" + StripBdRequireLines(current);

        if (patched == current) return true;

        FileUtils.WriteAllTextAtomic(indexJs, patched);
        Console.WriteLine($"[INFO] Patched {indexJs}");
        return true;
    }

    /// <summary>
    /// Discord 套用更新前，把這個 host 上所有 BetterDiscord 注入還原成原廠檔案。
    /// Discord 新版 updater 做 delta 更新時會驗證舊檔：
    ///   - index.js 被改過 → PatchFailed("expected hash ...")
    ///   - app.asar 被 BD 改名 → HostInstall 失敗 (NotFound)
    /// 兩者都會讓更新失敗，且 Discord 每次重啟都會從頭重試，常常卡到只能重裝。
    /// 必須在 Discord 沒有執行時呼叫。
    /// </summary>
    public bool RevertInjectionForUpdate(string discordPath)
    {
        bool changed = false;

        // 1. discord_desktop_core\index.js（本工具的注入）
        try
        {
            var coreDir = FindDesktopCoreDir(discordPath);
            var indexJs = coreDir == null ? null : Path.Combine(coreDir, "index.js");
            if (indexJs != null && File.Exists(indexJs))
            {
                var content = File.ReadAllText(indexJs);
                var stripped = StripBdRequireLines(content);
                if (stripped != content)
                {
                    FileUtils.WriteAllTextAtomic(indexJs, stripped);
                    Console.WriteLine($"[INFO] Restored original {indexJs}");
                    changed = true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to restore discord_desktop_core index.js: {ex.Message}");
        }

        // 2. resources\app + betterdiscord.app.asar（BetterDiscord 自己的注入），邏輯比照 BD 內建的 revert()
        try
        {
            var resources = _platform.GetResourcesPath(discordPath);
            var bdAppAsar = Path.Combine(resources, "betterdiscord.app.asar");
            var appAsar = Path.Combine(resources, "app.asar");
            var backupAsar = Path.Combine(resources, "app.asar.backup");
            var appDir = Path.Combine(resources, "app");
            var appIndexJs = Path.Combine(appDir, "index.js");

            if (File.Exists(bdAppAsar) && File.Exists(appIndexJs) &&
                File.ReadAllText(appIndexJs).Contains("betterdiscord", StringComparison.OrdinalIgnoreCase))
            {
                // 每一步都維持 Discord 可啟動的狀態：先把原廠 app.asar 放回去，再移除 app 資料夾
                bool restoredAppAsar = false;
                if (!File.Exists(appAsar))
                {
                    if (File.Exists(backupAsar)) File.Move(backupAsar, appAsar); // OpenAsar：原廠檔在 backup
                    else File.Copy(bdAppAsar, appAsar);
                    restoredAppAsar = true;
                }

                Directory.Delete(appDir, recursive: true);
                if (restoredAppAsar) File.Delete(bdAppAsar);

                Console.WriteLine($"[INFO] Restored original {appAsar}");
                changed = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to restore resources\\app.asar: {ex.Message}");
        }

        return changed;
    }

    private static string StripBdRequireLines(string content) => BdRequireLineRegex.Replace(content, "");

    /// <summary>
    /// 下載最新的 betterdiscord.asar，驗證完整性後才取代本地檔案
    /// </summary>
    public async Task<bool> UpdateBdAsarOnlyAsync(bool isCi)
    {
        try
        {
            if (isCi)
            {
                return await _gitHubService.DownloadLatestCiAsarAsync();
            }

            Console.WriteLine("[INFO] Downloading BetterDiscord Stable asar...");
            var data = await _http.GetByteArrayAsync(BdReleaseDownloadBase + "betterdiscord.asar");

            var hashes = AsarArchive.TryComputeFileHashes(data);
            if (hashes == null)
            {
                Console.WriteLine("[ERROR] Downloaded betterdiscord.asar is invalid. Keeping the existing file.");
                return false;
            }

            var remote = await FetchRemoteChecksumsAsync();
            if (remote != null && FindChangedFiles(hashes, remote).Count > 0)
            {
                Console.WriteLine("[ERROR] Downloaded betterdiscord.asar does not match checksums.txt. Keeping the existing file.");
                return false;
            }

            await FileUtils.ReplaceFileAsync(PathUtils.BdAsarPath, data);
            Console.WriteLine("[INFO] BetterDiscord asar updated.");

            var tag = await FetchLatestBdReleaseTagAsync();
            if (tag != null)
            {
                _configService.Config.LastInstalledBetterDiscordVersion = tag;
                _configService.SaveSettings();
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to update BetterDiscord asar: {ex.Message}");
            return false;
        }
    }

    public async Task<string?> FetchLatestBdReleaseTagAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, BdLatestReleaseUrl);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? "";
            return finalUrl.Contains("/tag/") ? finalUrl.Split('/').Last() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 判斷是否需要（重新）下載 betterdiscord.asar：
    ///   - 本地檔案不存在或損毀 → 需要（重跑腳本就能修復壞掉的 BD）
    ///   - 與最新 release 的 checksums.txt 不同 → 需要
    /// 不用 release tag 和 misc.json 比對：BD 曾在不改 tag 的情況下重新上傳 asar（v1.14.5 的 asset 實際是 1.14.6），
    /// 兩邊永遠對不起來，會造成每次執行都判定有更新、每次都把 Discord 關掉。
    /// </summary>
    public async Task<bool> CheckForBdUpdatesAsync(bool isCi)
    {
        string asarPath = GetAsarPath(isCi);
        var localHashes = AsarArchive.TryComputeFileHashes(asarPath);
        if (localHashes == null)
        {
            Console.WriteLine(File.Exists(asarPath)
                ? "[WARN] Local BetterDiscord asar is corrupted. It will be re-downloaded."
                : "[INFO] BetterDiscord asar not found. It will be downloaded.");
            return true;
        }

        if (isCi)
        {
            return await _gitHubService.CheckForCiUpdatesAsync();
        }

        var remote = await FetchRemoteChecksumsAsync();
        if (remote == null)
        {
            // 無法確認時不要動 Discord
            Console.WriteLine("[WARN] Could not fetch BetterDiscord checksums. Skipping BetterDiscord update check.");
            return false;
        }

        var changed = FindChangedFiles(localHashes, remote);
        if (changed.Count == 0)
        {
            Console.WriteLine("BetterDiscord asar matches the latest release.");
            return false;
        }

        Console.WriteLine($"[INFO] BetterDiscord update available (changed: {string.Join(", ", changed)}).");
        return true;
    }

    private static List<string> FindChangedFiles(Dictionary<string, string> local, Dictionary<string, string> remote)
    {
        return remote
            .Where(kv => !local.TryGetValue(kv.Key, out var hash) || !hash.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// 讀取 release 附帶的 checksums.txt（asar 內每個檔案的 SHA256，格式："&lt;sha256&gt;  &lt;path&gt;"）
    /// </summary>
    private async Task<Dictionary<string, string>?> FetchRemoteChecksumsAsync()
    {
        if (_remoteChecksums != null) return _remoteChecksums;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var text = await _http.GetStringAsync(BdReleaseDownloadBase + "checksums.txt", cts.Token);

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in text.Split('\n'))
            {
                var parts = rawLine.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || parts[0].Length != 64) continue;
                result[parts[1].Trim().TrimStart('*').Replace('\\', '/')] = parts[0].ToLowerInvariant();
            }

            return _remoteChecksums = result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to fetch BetterDiscord checksums: {ex.Message}");
            return null;
        }
    }

    public string? GetLocalInstalledBdVersion()
    {
        try
        {
            if (File.Exists(PathUtils.BdMiscPathPath))
            {
                string json = File.ReadAllText(PathUtils.BdMiscPathPath);
                var misc = JsonSerializer.Deserialize<BetterDiscordMisc>(json);
                if (!string.IsNullOrEmpty(misc?.Version))
                {
                    return misc.Version;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARNING] Failed to read local BetterDiscord version file: {ex.Message}");
        }

        // Fallback to Config
        string? configVersion = _configService.Config.LastInstalledBetterDiscordVersion;
        if (!string.IsNullOrEmpty(configVersion))
        {
            return configVersion;
        }

        // Output message when no previous installation is found
        Console.WriteLine("[INFO] No previous BetterDiscord version found.");
        return null;
    }
}
