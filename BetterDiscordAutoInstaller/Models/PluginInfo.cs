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

    public bool IsInstalled() => File.Exists(SavePath);
}