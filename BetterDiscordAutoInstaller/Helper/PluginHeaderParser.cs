using System.Text.RegularExpressions;

namespace BetterDiscordAutoInstaller.Helper;

public static class PluginHeaderParser
{
    // 匹配包含 "@version 1.0.0" 格式的正規表達式
    private static readonly Regex VersionRegex = new Regex(@"@version\s+(?<version>[\d\.]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 從字串內容（JS 原始碼）解析 @version 號碼
    /// </summary>
    public static string? ExtractVersion(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var match = VersionRegex.Match(content);
        if (match.Success)
        {
            return match.Groups["version"].Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// 從本地檔案路徑解析 @version 號碼
    /// </summary>
    public static string? ExtractVersionFromFile(string filePath)
    {
        if (!File.Exists(filePath)) return null;

        try
        {
            // 只需要讀取前面幾行（標頭區域），無需讀取整個檔案
            using var stream = File.OpenRead(filePath);
            using var reader = new StreamReader(stream);

            string? line;
            int linesRead = 0;
            // 限制讀取前 50 行，提升效能
            while ((line = reader.ReadLine()) != null && linesRead < 50)
            {
                var match = VersionRegex.Match(line);
                if (match.Success)
                {
                    return match.Groups["version"].Value.Trim();
                }
                linesRead++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to read version from local file: {ex.Message}");
        }

        return null;
    }
}