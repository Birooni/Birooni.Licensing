using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Birooni.Licensing.Api.Models;

[Table("validation_logs")]
public class ValidationLog
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("id")]
    public long Id { get; set; }

    [Column("license_id")]
    public Guid? LicenseId { get; set; }

    [Required]
    [MaxLength(200)]
    [Column("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    [Column("status")]
    public string Status { get; set; } = string.Empty;

    [MaxLength(100)]
    [Column("ip_address")]
    public string? IpAddress { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation property
    [ForeignKey(nameof(LicenseId))]
    public virtual License? License { get; set; }
}
