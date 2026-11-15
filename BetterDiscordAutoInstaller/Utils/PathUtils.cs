using BetterDiscordAutoInstaller.Models;
using BetterDiscordAutoInstaller.Platform;

namespace BetterDiscordAutoInstaller.Utils;

public static class PathUtils
{
    public const string BdaiVersion = "1.0.0";
    public const string SettingsFileName = "settings.json";
    public const string GitHubTokenFileName = "github_token";

    // Windows：%AppData%\BetterDiscord；macOS：~/Library/Application Support/BetterDiscord
    public static string BdFolder => PlatformService.Current.BetterDiscordFolder;

    public static string BdAsarPath => Path.Combine(BdFolder, "data", "betterdiscord.asar");
    public static string BdCiAsarPath => Path.Combine(BdFolder, "data", "betterdiscord-ci.asar");
    public static string BdMiscPathPath => Path.Combine(BdFolder, "data", "stable", "misc.json");
    public static string BdPluginsPath => Path.Combine(BdFolder, "plugins");

    public static string GetDefaultDiscordPath(DiscordEdition edition) => PlatformService.Current.GetDefaultDiscordPath(edition);
}
