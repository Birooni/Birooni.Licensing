using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Birooni.Licensing.Api.Models;

[Table("plugin_installs")]
public class PluginInstall
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(300)]
    [Column("install_key")]
    public string InstallKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    [Column("product")]
    public string Product { get; set; } = string.Empty;

    [MaxLength(200)]
    [Column("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(200)]
    [Column("device_name")]
    public string DeviceName { get; set; } = string.Empty;

    [MaxLength(50)]
    [Column("plugin_version")]
    public string PluginVersion { get; set; } = string.Empty;

    [MaxLength(50)]
    [Column("revit_version")]
    public string RevitVersion { get; set; } = string.Empty;

    [MaxLength(100)]
    [Column("ip_address")]
    public string? IpAddress { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("first_seen")]
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;

    [Column("last_seen")]
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
}
