using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Utils;
using System.Text.Json;

namespace BetterDiscordAutoInstaller.Services;

public class BetterDiscordService
{
    private readonly HttpClient _http = new();
    private readonly ConfigService _configService;
    private readonly GitHubService _gitHubService;

    public BetterDiscordService(ConfigService configService, GitHubService gitHubService)
    {
        _configService = configService;
        _gitHubService = gitHubService;
    }

    public string GetAsarPath(bool isCi) => isCi ? PathUtils.BdCiAsarPath : PathUtils.BdAsarPath;

    public string GetRequireLine(bool isCi) => $"require(\"{GetAsarPath(isCi).Replace("\\", "/")}\");\n";

    public bool IsBdInjected(string discordPath, bool isCi)
    {
        var modulesPath = Path.Combine(discordPath, "modules");
        if (!Directory.Exists(modulesPath)) return false;

        var coreDirs = Directory.GetDirectories(modulesPath, "discord_desktop_core-*");
        if (coreDirs.Length == 0) return false;

        var indexJs = Path.Combine(coreDirs[0], "discord_desktop_core", "index.js");
        if (!File.Exists(indexJs)) return false;

        var content = File.ReadAllText(indexJs);
        var expectedRequire = $"require(\"{GetAsarPath(isCi).Replace("\\", "/")}\");";
        return content.Contains(expectedRequire);
    }

    public void InjectPatch(string discordPath, bool isCi)
    {
        var modulesPath = Path.Combine(discordPath, "modules");
        var coreDirs = Directory.GetDirectories(modulesPath, "discord_desktop_core-*");
        if (coreDirs.Length == 0)
            throw new DirectoryNotFoundException("No discord_desktop_core folder found.");

        var indexJs = Path.Combine(coreDirs[0], "discord_desktop_core", "index.js");
        var requireLine = GetRequireLine(isCi).Trim();
        var otherRequireLine = GetRequireLine(!isCi).Trim();

        // 情況 A：如果 index.js 還不存在（乾淨的 Discord），自動建立並寫入預設載入檔
        if (!File.Exists(indexJs))
        {
            var initialLines = new List<string>
        {
            requireLine,
            "module.exports = require('./core.asar');"
        };
            File.WriteAllLines(indexJs, initialLines);
            Console.WriteLine($"[INFO] Created and patched {indexJs}");
            return;
        }

        // 情況 B：如果 index.js 已經存在，執行替換/更新邏輯
        var lines = File.ReadAllLines(indexJs).ToList();
        lines.RemoveAll(l => l.Trim() == otherRequireLine);

        if (!lines.Any(l => l.Trim() == requireLine))
        {
            lines.Insert(0, requireLine);
            File.WriteAllLines(indexJs, lines);
            Console.WriteLine($"[INFO] Patched {indexJs}");
        }
    }

    public async Task UpdateBdAsarOnlyAsync(bool isCi)
    {
        if (isCi)
        {
            await _gitHubService.DownloadLatestCiAsarAsync();
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(PathUtils.BdAsarPath)!);
        Console.WriteLine("[INFO] Downloading BetterDiscord Stable asar...");

        var data = await _http.GetByteArrayAsync("https://github.com/rauenzi/BetterDiscordApp/releases/latest/download/betterdiscord.asar");
        await File.WriteAllBytesAsync(PathUtils.BdAsarPath, data);

        _configService.Config.LastInstalledBetterDiscordVersion = await FetchLatestBdReleaseTagAsync();
        _configService.SaveSettings();
    }

    public async Task<string> FetchLatestBdReleaseTagAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "https://github.com/rauenzi/BetterDiscordApp/releases/latest");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? "";
        return finalUrl.Split('/').LastOrDefault() ?? "unknown";
    }

    public async Task<bool> CheckForBdUpdatesAsync(bool isCi)
    {
        if (isCi)
        {
            return await _gitHubService.CheckForCiUpdatesAsync();
        }

        // 1. 從本地 stable 檔案讀取目前安裝的版本號
        string? localVersion = GetLocalInstalledBdVersion();
        if (string.IsNullOrWhiteSpace(localVersion)) return false;

        // 2. 從 GitHub 抓最新的 Stable 版本號 (例如 "v1.14.1" 或 "1.14.1")
        string latestVersion = await FetchLatestBdReleaseTagAsync();

        // 3. 處理前綴 'v' 的影響後進行比對 (如 "v1.14.1" vs "1.14.1")
        if (!string.IsNullOrEmpty(latestVersion))
        {
            return !latestVersion
                .TrimStart('v')
                .Equals(localVersion.TrimStart('v'), StringComparison.OrdinalIgnoreCase);
        }

        return false;
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