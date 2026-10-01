using System.Text.Json.Serialization;

namespace BetterDiscordAutoInstaller.Models;

public class DiscordManifest
{
    [JsonPropertyName("full")]
    public ManifestFullInfo? Full { get; set; }
}

public class ManifestFullInfo
{
    [JsonPropertyName("host_version")]
    public int[]? HostVersion { get; set; }

    /// 
    /// 將 host_version 陣列 [1, 0, 9260] 轉為 "1.0.9260" 字串
    /// 
    public string GetVersionString()
    {
        if (HostVersion == null || HostVersion.Length == 0)
            return string.Empty;

        return string.Join(".", HostVersion);
    }
}