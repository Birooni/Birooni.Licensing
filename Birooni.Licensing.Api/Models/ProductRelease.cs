using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Birooni.Licensing.Api.Models;

[Table("product_releases")]
public class ProductRelease
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    [Column("product")]
    public string Product { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    [Column("version")]
    public string Version { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    [Column("revit_version")]
    public string RevitVersion { get; set; } = "All"; // "2025", "2026", "2027", or "All"

    [Required]
    [MaxLength(500)]
    [Column("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [Column("release_notes")]
    public string ReleaseNotes { get; set; } = string.Empty;

    [Column("is_mandatory")]
    public bool IsMandatory { get; set; } = false;

    [MaxLength(128)]
    [Column("checksum_sha256")]
    public string? ChecksumSha256 { get; set; }

    [Column("released_at")]
    public DateTimeOffset ReleasedAt { get; set; } = DateTimeOffset.UtcNow;
}
