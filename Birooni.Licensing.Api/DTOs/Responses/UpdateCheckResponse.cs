namespace Birooni.Licensing.Api.DTOs.Responses;

public class UpdateCheckResponse
{
    public bool UpdateAvailable { get; set; }
    public string Product { get; set; } = string.Empty;
    public string CurrentVersion { get; set; } = string.Empty;
    public string LatestVersion { get; set; } = string.Empty;
    public string? DownloadUrl { get; set; }
    public string? ReleaseNotes { get; set; }
    public bool IsMandatory { get; set; }
    public string? ChecksumSha256 { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}
