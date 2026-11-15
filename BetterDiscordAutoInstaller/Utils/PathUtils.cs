using BetterDiscordAutoInstaller.Models;

namespace BetterDiscordAutoInstaller.Utils;

public static class PathUtils
{
    // 由 csproj 在 Release 編譯時產生的版本號（Major.Minor.天數.時分）取前三段，與 GitHub release tag 對應，不需手動更新
    public static readonly string BdaiVersion = GetBdaiVersion();

    private static string GetBdaiVersion()
    {
        var version = typeof(PathUtils).Assembly.GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
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