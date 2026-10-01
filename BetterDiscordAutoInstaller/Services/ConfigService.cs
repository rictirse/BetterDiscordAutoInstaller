using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Utils;
using System.Text.Json;

namespace BetterDiscordAutoInstaller.Services;

public class ConfigService
{
    public AppConfig Config { get; private set; } = new();
    public string? GitHubToken { get; private set; }

    public void LoadSettings()
    {
        if (!File.Exists(PathUtils.SettingsFileName)) return;
        try
        {
            var json = File.ReadAllText(PathUtils.SettingsFileName);
            Config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to load settings: {ex.Message}");
        }
    }

    public void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathUtils.SettingsFileName, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to save settings: {ex.Message}");
        }
    }

    public void LoadGitHubToken()
    {
        if (!File.Exists(PathUtils.GitHubTokenFileName)) return;
        try
        {
            var token = File.ReadAllText(PathUtils.GitHubTokenFileName).Trim('"').Trim();
            GitHubToken = token;
        }
        catch
        {
            // Ignore token read error
        }
    }

    public void SaveGitHubToken(string token)
    {
        GitHubToken = token;
        File.WriteAllText(PathUtils.GitHubTokenFileName, JsonSerializer.Serialize(token));
    }

    public string? GetLastInstalledVersion(DiscordEdition edition) => edition switch
    {
        DiscordEdition.Discord => Config.LastInstalledDiscordVersion,
        DiscordEdition.DiscordCanary => Config.LastInstalledDiscordCanaryVersion,
        DiscordEdition.DiscordPTB => Config.LastInstalledDiscordPtbVersion,
        _ => null
    };

    public void SetDiscordVersion(DiscordEdition edition, string version)
    {
        switch (edition)
        {
            case DiscordEdition.Discord: Config.LastInstalledDiscordVersion = version; break;
            case DiscordEdition.DiscordCanary: Config.LastInstalledDiscordCanaryVersion = version; break;
            case DiscordEdition.DiscordPTB: Config.LastInstalledDiscordPtbVersion = version; break;
        }
    }
}