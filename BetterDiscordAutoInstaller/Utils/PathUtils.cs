using BetterDiscordAutoInstaller.Models;

namespace BetterDiscordAutoInstaller.Utils;

public static class PathUtils
{
    public const string BdaiVersion = "1.0.0";
    public const string SettingsFileName = "settings.json";
    public const string GitHubTokenFileName = "github_token";

    public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string BdAsarPath => Path.Combine(AppData, "BetterDiscord", "data", "betterdiscord.asar");
    public static string BdCiAsarPath => Path.Combine(AppData, "BetterDiscord", "data", "betterdiscord-ci.asar");
    public static string BdMiscPathPath => Path.Combine(AppData, "BetterDiscord", "data", "stable", "misc.json");

    public static string GetDefaultDiscordPath(DiscordEdition edition)
    {
        return Path.Combine(LocalAppData, edition.ToString());
    }
}