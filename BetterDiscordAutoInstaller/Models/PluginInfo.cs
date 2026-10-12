using BetterDiscordAutoInstaller.Helper;

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
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        SavePath = Path.Combine(appData, "BetterDiscord", "plugins", fileName);
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