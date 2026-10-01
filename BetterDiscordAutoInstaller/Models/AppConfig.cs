using System.Text.Json.Serialization;

namespace BetterDiscordAutoInstaller.Models;

public class AppConfig
{
    [JsonPropertyName("discord_installed_path")]
    public string? DiscordInstalledPath { get; set; }

    [JsonPropertyName("discord_canary_installed_path")]
    public string? DiscordCanaryInstalledPath { get; set; }

    [JsonPropertyName("discord_ptb_installed_path")]
    public string? DiscordPtbInstalledPath { get; set; }

    [JsonPropertyName("disable_bdai_autoupdate")]
    public bool DisableBdaiAutoupdate { get; set; } = false;

    [JsonPropertyName("use_betterdiscord_ci_releases")]
    public bool UseBetterDiscordCiReleases { get; set; } = false;

    [JsonPropertyName("workflow_runs_limit")]
    public int WorkflowRunsLimit { get; set; } = 5;

    [JsonPropertyName("rerun_discord_edition")]
    public string RerunDiscordEdition { get; set; } = "Discord";

    [JsonPropertyName("disable_version_check")]
    public bool DisableVersionCheck { get; set; } = false;

    [JsonPropertyName("last_installed_discord_version")]
    public string? LastInstalledDiscordVersion { get; set; }

    [JsonPropertyName("last_installed_discord_canary_version")]
    public string? LastInstalledDiscordCanaryVersion { get; set; }

    [JsonPropertyName("last_installed_discord_ptb_version")]
    public string? LastInstalledDiscordPtbVersion { get; set; }

    [JsonPropertyName("last_installed_betterdiscord_version")]
    public string? LastInstalledBetterDiscordVersion { get; set; }

    [JsonPropertyName("last_installed_betterdiscord_ci_version")]
    public long? LastInstalledBetterDiscordCiVersion { get; set; }
}