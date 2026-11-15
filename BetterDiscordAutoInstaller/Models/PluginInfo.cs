using BetterDiscordAutoInstaller.Helper;
using BetterDiscordAutoInstaller.Utils;

namespace BetterDiscordAutoInstaller.Models;

public class PluginInfo
{
    public string Url { get; set; }
    public string SavePath { get; set; }
    public string Name { get; set; }

    public PluginInfo(string url)
    {
        Url = url;
        var fileName = Path.GetFileName(url);
        // macOS 的 SpecialFolder.ApplicationData 是 ~/.config，BetterDiscord 資料夾位置交給 PathUtils（依平台決定）
        SavePath = Path.Combine(PathUtils.BdPluginsPath, fileName);
        Name = fileName.Replace(".plugin.js", "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取得目前本地已安裝的版本號
    /// </summary>
    public string? GetInstalledVersion()
    {
        return PluginHeaderParser.ExtractVersionFromFile(SavePath);
    }

    /// <summary>
    /// 判斷檔案是否存在
    /// </summary>
    public bool FileExists => File.Exists(SavePath);
}