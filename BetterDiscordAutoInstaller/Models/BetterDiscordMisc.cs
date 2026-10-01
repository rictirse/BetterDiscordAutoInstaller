using System.Text.Json.Serialization;

namespace BetterDiscordAutoInstaller.Models;

public class BetterDiscordMisc
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}