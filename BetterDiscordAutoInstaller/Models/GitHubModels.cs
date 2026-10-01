using System.Text.Json.Serialization;

namespace BetterDiscordAutoInstaller.Models;

public class GitHubWorkflowRunResponse
{
    [JsonPropertyName("workflow_runs")]
    public GitHubWorkflowRun[]? WorkflowRuns { get; set; }
}

public class GitHubWorkflowRun
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; set; }
}

public class GitHubArtifactsResponse
{
    [JsonPropertyName("artifacts")]
    public GitHubArtifact[]? Artifacts { get; set; }
}

public class GitHubArtifact
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("archive_download_url")]
    public string? ArchiveDownloadUrl { get; set; }
}