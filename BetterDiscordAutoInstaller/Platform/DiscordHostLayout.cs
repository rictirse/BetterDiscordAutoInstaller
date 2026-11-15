namespace BetterDiscordAutoInstaller.Platform;

/// <summary>
/// Discord（Windows 版 Squirrel / 新版 updater）安裝目錄結構的純邏輯：
///   - &lt;安裝目錄&gt;\app-x.x.x（更新中的 ".app-x.x.x.installing" 不算）
///   - app-x.x.x\modules\discord_desktop_core-N\discord_desktop_core
/// 只做路徑與檔案系統查詢，不呼叫任何 Windows 專屬 API，所以不標 SupportedOSPlatform（測試可在任何平台執行）。
/// 實作是從 WindowsPlatformService 原封不動搬過來的，行為不變。
/// </summary>
internal static class DiscordHostLayout
{
    internal static Version? ParseAppFolderVersion(string folderName)
    {
        var versionPart = folderName.StartsWith("app-", StringComparison.OrdinalIgnoreCase) ? folderName[4..] : folderName;
        return Version.TryParse(versionPart, out var version) ? version : null;
    }

    internal static string GetLatestInstalledDiscordFolder(string parentPath)
    {
        if (!Directory.Exists(parentPath))
            throw new DirectoryNotFoundException($"Discord path not found: {parentPath}");

        // 只會比對 "app-*"，不會抓到更新中的 ".app-x.x.x.installing"
        var appDirs = Directory.GetDirectories(parentPath, "app-*")
                               .Select(Path.GetFileName)
                               .OfType<string>()
                               .ToList();

        if (appDirs.Count == 0)
            throw new DirectoryNotFoundException($"No 'app-*' folder found in: {parentPath}");

        // 依版本號排序；字串排序會讓 app-1.0.10000 排在 app-1.0.9999 前面
        return appDirs
            .OrderBy(name => ParseAppFolderVersion(name) ?? new Version(0, 0))
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Last();
    }

    /// <summary>
    /// 取得 discord_desktop_core 模組資料夾；有多個版本時取編號最大的（Discord 載入的那一個）
    /// </summary>
    internal static string? FindDesktopCoreDir(string hostPath)
    {
        var modulesPath = Path.Combine(hostPath, "modules");
        if (!Directory.Exists(modulesPath)) return null;

        const string prefix = "discord_desktop_core-";
        var latest = Directory.GetDirectories(modulesPath, prefix + "*")
            .Select(dir => (Dir: dir, Num: int.TryParse(Path.GetFileName(dir)[prefix.Length..], out var n) ? n : -1))
            .OrderBy(x => x.Num)
            .LastOrDefault();

        return latest.Dir == null ? null : Path.Combine(latest.Dir, "discord_desktop_core");
    }

    internal static bool IsInsideDirectory(string file, string directory)
    {
        try
        {
            var fullFile = Path.GetFullPath(file);
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullFile.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
