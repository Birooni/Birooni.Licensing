using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Birooni.Licensing.Api.Models;

[Table("floating_sessions")]
public class FloatingSession
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Column("license_id")]
    public Guid LicenseId { get; set; }

    [Required]
    [MaxLength(200)]
    [Column("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(200)]
    [Column("user_name")]
    public string UserName { get; set; } = string.Empty;

    [MaxLength(50)]
    [Column("plugin_version")]
    public string PluginVersion { get; set; } = string.Empty;

    [Column("started_at")]
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("last_heartbeat_at")]
    public DateTimeOffset LastHeartbeatAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    // Navigation property
    public virtual License? License { get; set; }
}
