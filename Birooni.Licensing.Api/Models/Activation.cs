using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Birooni.Licensing.Api.Models;

[Table("activations")]
public class Activation
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [Column("license_id")]
    public Guid LicenseId { get; set; }

    [Required]
    [MaxLength(200)]
    [Column("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(200)]
    [Column("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [MaxLength(50)]
    [Column("plugin_version")]
    public string PluginVersion { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("activated_at")]
    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("last_validated_at")]
    public DateTimeOffset LastValidatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation property
    [ForeignKey(nameof(LicenseId))]
    public virtual License? License { get; set; }
}
