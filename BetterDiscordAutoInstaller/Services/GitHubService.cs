using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Utils;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BetterDiscordAutoInstaller.Services;

public class GitHubService
{
    private readonly HttpClient _http = new();
    private readonly ConfigService _configService;
    private readonly bool _allowTokenPrompt;

    /// <param name="allowTokenPrompt">沒有 token 時能不能在主控台詢問。開機自動執行時沒有人能輸入，要傳 false，否則會一直卡在等輸入</param>
    public GitHubService(ConfigService configService, bool allowTokenPrompt = true)
    {
        _configService = configService;
        _allowTokenPrompt = allowTokenPrompt;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BetterDiscordAutoInstaller", PathUtils.BdaiVersion));
    }

    /// <summary>
    /// 設定 GitHub API 的認證標頭。沒有 token（而且不能詢問，或使用者沒輸入）時回傳 false，呼叫端要跳過 CI 下載
    /// </summary>
    private bool TryEnsureHeaders()
    {
        if (_configService.GitHubToken == null) _configService.LoadGitHubToken();

        if (string.IsNullOrEmpty(_configService.GitHubToken) && _allowTokenPrompt)
        {
            Console.Write("Enter GitHub Access Token for CI downloads: ");
            var token = Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(token)) _configService.SaveGitHubToken(token);
        }

        if (string.IsNullOrEmpty(_configService.GitHubToken))
        {
            Console.WriteLine($"[WARN] use_betterdiscord_ci_releases needs a GitHub token. Skipping the BetterDiscord CI update. Run the tool from the menu (option 3) once to enter it, or save it in the {PathUtils.GitHubTokenFileName} file next to the program.");
            return false;
        }

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _configService.GitHubToken);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return true;
    }

    public async Task<bool> CheckForCiUpdatesAsync()
    {
        if (!TryEnsureHeaders()) return false;
        try
        {
            var url = $"https://api.github.com/repos/BetterDiscord/BetterDiscord/actions/workflows/ci.yml/runs?per_page={_configService.Config.WorkflowRunsLimit}";
            var json = await _http.GetStringAsync(url);
            var res = JsonSerializer.Deserialize<GitHubWorkflowRunResponse>(json);

            var latestRun = res?.WorkflowRuns?.FirstOrDefault(r => r.Name == "BetterDiscord CI" && r.Conclusion == "success");
            if (latestRun != null)
            {
                return latestRun.Id != _configService.Config.LastInstalledBetterDiscordCiVersion;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] GitHub API check failed: {ex.Message}");
        }
        return false;
    }

    public async Task<bool> DownloadLatestCiAsarAsync()
    {
        if (!TryEnsureHeaders()) return false;
        var url = $"https://api.github.com/repos/BetterDiscord/BetterDiscord/actions/workflows/ci.yml/runs?per_page={_configService.Config.WorkflowRunsLimit}";
        var json = await _http.GetStringAsync(url);
        var res = JsonSerializer.Deserialize<GitHubWorkflowRunResponse>(json);

        var latestRun = res?.WorkflowRuns?.FirstOrDefault(r => r.Name == "BetterDiscord CI" && r.Conclusion == "success");
        if (latestRun == null) return false;

        var artUrl = $"https://api.github.com/repos/BetterDiscord/BetterDiscord/actions/runs/{latestRun.Id}/artifacts";
        var artJson = await _http.GetStringAsync(artUrl);
        var artRes = JsonSerializer.Deserialize<GitHubArtifactsResponse>(artJson);

        var artifact = artRes?.Artifacts?.FirstOrDefault(a => a.Name?.Equals("artifact", StringComparison.OrdinalIgnoreCase) == true);
        if (artifact?.ArchiveDownloadUrl == null) return false;

        var zipBytes = await _http.GetByteArrayAsync(artifact.ArchiveDownloadUrl);
        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms);

        var asarEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".asar"));
        if (asarEntry == null) return false;

        using var asarBuffer = new MemoryStream();
        await using (var entryStream = asarEntry.Open())
        {
            await entryStream.CopyToAsync(asarBuffer);
        }

        var asarBytes = asarBuffer.ToArray();
        if (AsarArchive.TryComputeFileHashes(asarBytes) == null)
        {
            Console.WriteLine("[ERROR] Downloaded BetterDiscord CI asar is invalid. Keeping the existing file.");
            return false;
        }

        // 先完整寫入暫存檔再取代，避免留下半個 asar
        await FileUtils.ReplaceFileAsync(PathUtils.BdCiAsarPath, asarBytes);
        _configService.Config.LastInstalledBetterDiscordCiVersion = latestRun.Id;
        _configService.SaveSettings();
        Console.WriteLine("[INFO] BetterDiscord CI asar updated successfully.");
        return true;
    }
}