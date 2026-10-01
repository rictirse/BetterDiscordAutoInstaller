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

    public GitHubService(ConfigService configService)
    {
        _configService = configService;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BetterDiscordAutoInstaller", PathUtils.BdaiVersion));
    }

    private void EnsureHeaders()
    {
        if (_configService.GitHubToken == null) _configService.LoadGitHubToken();

        if (string.IsNullOrEmpty(_configService.GitHubToken))
        {
            Console.Write("Enter GitHub Access Token for CI downloads: ");
            var token = Console.ReadLine()?.Trim() ?? "";
            _configService.SaveGitHubToken(token);
        }

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _configService.GitHubToken);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<bool> CheckForCiUpdatesAsync()
    {
        EnsureHeaders();
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

    public async Task DownloadLatestCiAsarAsync()
    {
        EnsureHeaders();
        var url = $"https://api.github.com/repos/BetterDiscord/BetterDiscord/actions/workflows/ci.yml/runs?per_page={_configService.Config.WorkflowRunsLimit}";
        var json = await _http.GetStringAsync(url);
        var res = JsonSerializer.Deserialize<GitHubWorkflowRunResponse>(json);

        var latestRun = res?.WorkflowRuns?.FirstOrDefault(r => r.Name == "BetterDiscord CI" && r.Conclusion == "success");
        if (latestRun == null) return;

        var artUrl = $"https://api.github.com/repos/BetterDiscord/BetterDiscord/actions/runs/{latestRun.Id}/artifacts";
        var artJson = await _http.GetStringAsync(artUrl);
        var artRes = JsonSerializer.Deserialize<GitHubArtifactsResponse>(artJson);

        var artifact = artRes?.Artifacts?.FirstOrDefault(a => a.Name?.Equals("artifact", StringComparison.OrdinalIgnoreCase) == true);
        if (artifact?.ArchiveDownloadUrl == null) return;

        var zipBytes = await _http.GetByteArrayAsync(artifact.ArchiveDownloadUrl);
        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms);

        var asarEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".asar"));
        if (asarEntry != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathUtils.BdCiAsarPath)!);
            asarEntry.ExtractToFile(PathUtils.BdCiAsarPath, overwrite: true);
            _configService.Config.LastInstalledBetterDiscordCiVersion = latestRun.Id;
            _configService.SaveSettings();
            Console.WriteLine("[INFO] BetterDiscord CI asar updated successfully.");
        }
    }
}