using System.ComponentModel.DataAnnotations;

namespace Birooni.Licensing.Api.DTOs.Requests;

public class CreateReleaseRequest
{
    [Required]
    [MaxLength(100)]
    public string Product { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Version { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string RevitVersion { get; set; } = "All"; // "2025", "2026", "2027", or "All"

    [Required]
    [Url]
    [MaxLength(500)]
    public string DownloadUrl { get; set; } = string.Empty;

    public string ReleaseNotes { get; set; } = string.Empty;

    public bool IsMandatory { get; set; } = false;

    public string? ChecksumSha256 { get; set; }
}
